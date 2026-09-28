//! The pipe server and client talking to each other over real named pipes.

use std::io::ErrorKind;
use std::time::Duration;

use cabinetos_ipc::{IpcError, PipeClient, PipeName, PipeServer};
use cabinetos_protocol::{Envelope, PROTOCOL_VERSION, Request, RequestId, Response};
use tokio::net::windows::named_pipe::{ClientOptions, ServerOptions};

const TIMEOUT: Duration = Duration::from_secs(5);

/// Answers `ping` with `pong` on every connection, like a tiny core.
fn spawn_pong_server(mut server: PipeServer) -> tokio::task::JoinHandle<()> {
    tokio::spawn(async move {
        loop {
            let Ok(mut connection) = server.accept().await else {
                return;
            };
            tokio::spawn(async move {
                while let Ok(request) = connection.recv::<Envelope<Request>>().await {
                    let reply = Envelope::new(
                        request.id,
                        Response::Pong {
                            protocol_version: PROTOCOL_VERSION,
                            core_version: "test".to_owned(),
                        },
                    );
                    if connection.send(&reply).await.is_err() {
                        return;
                    }
                }
            });
        }
    })
}

#[tokio::test]
async fn a_request_gets_a_reply_with_the_same_id() {
    let name = PipeName::random();
    let server = spawn_pong_server(PipeServer::bind(&name).unwrap());

    let mut client = PipeClient::connect(&name, TIMEOUT).await.unwrap();
    let id = RequestId::new();
    let reply = client
        .request_with_id(id.clone(), Request::Ping)
        .await
        .unwrap();
    assert_eq!(reply.id, id);
    assert!(matches!(
        reply.body,
        Response::Pong { protocol_version, .. } if protocol_version == PROTOCOL_VERSION
    ));
    server.abort();
}

#[tokio::test]
async fn several_clients_are_served_at_once() {
    let name = PipeName::random();
    let server = spawn_pong_server(PipeServer::bind(&name).unwrap());

    let mut first = PipeClient::connect(&name, TIMEOUT).await.unwrap();
    let mut second = PipeClient::connect(&name, TIMEOUT).await.unwrap();
    for _ in 0..3 {
        first.request(Request::Ping).await.unwrap();
        second.request(Request::Ping).await.unwrap();
    }
    server.abort();
}

#[tokio::test]
async fn a_second_server_cannot_take_the_same_name() {
    let name = PipeName::random();
    let _first = PipeServer::bind(&name).unwrap();
    assert!(PipeServer::bind(&name).is_err());
}

#[tokio::test]
async fn connecting_to_a_missing_pipe_fails_at_once() {
    let started = std::time::Instant::now();
    let result = PipeClient::connect(&PipeName::random(), TIMEOUT).await;
    assert!(matches!(result, Err(IpcError::Io(error)) if error.kind() == ErrorKind::NotFound));
    assert!(started.elapsed() < Duration::from_secs(1));
}

#[tokio::test]
async fn a_busy_pipe_is_retried_until_an_instance_frees_up() {
    // A plain pipe with one instance, taken by a first client.
    let name = PipeName::random();
    let only_instance = ServerOptions::new()
        .first_pipe_instance(true)
        .create(name.as_str())
        .unwrap();
    let _first_client = ClientOptions::new().open(name.as_str()).unwrap();
    only_instance.connect().await.unwrap();

    let busy = PipeClient::connect(&name, Duration::from_millis(200)).await;
    assert!(matches!(busy, Err(IpcError::Timeout(_))), "{busy:?}");

    // A new instance appears while the client is retrying.
    let pipe_name = name.clone();
    let late_instance = tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(150)).await;
        let instance = ServerOptions::new().create(pipe_name.as_str()).unwrap();
        instance.connect().await.unwrap();
        instance
    });
    PipeClient::connect(&name, TIMEOUT).await.unwrap();
    late_instance.await.unwrap();
}

#[tokio::test]
async fn a_reply_with_another_id_is_a_protocol_error() {
    let name = PipeName::random();
    let mut server = PipeServer::bind(&name).unwrap();
    let liar = tokio::spawn(async move {
        let mut connection = server.accept().await.unwrap();
        let _request: Envelope<Request> = connection.recv().await.unwrap();
        let reply = Envelope::new(RequestId::new(), Response::Ok);
        connection.send(&reply).await.unwrap();
        // Keep the connection open until the client has read the reply.
        let _ = connection.read_frame().await;
    });

    let mut client = PipeClient::connect(&name, TIMEOUT).await.unwrap();
    let result = client.request(Request::Ping).await;
    assert!(matches!(result, Err(IpcError::Protocol(_))), "{result:?}");
    drop(client);
    liar.await.unwrap();
}
