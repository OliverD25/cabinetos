//! The pipe server and client talking to each other over real named pipes.

use std::io::ErrorKind;
use std::time::Duration;

use cabinetos_ipc::{IpcError, PipeClient, PipeName, PipeServer, SharedSection};
use cabinetos_protocol::{Envelope, Event, PROTOCOL_VERSION, Request, RequestId, Response};
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

#[tokio::test]
async fn the_server_sees_the_client_process() {
    let name = PipeName::random();
    let mut server = PipeServer::bind(&name).unwrap();
    let accepted = tokio::spawn(async move {
        let connection = server.accept().await.unwrap();
        connection.client_process_id().unwrap()
    });
    let _client = PipeClient::connect(&name, TIMEOUT).await.unwrap();
    assert_eq!(accepted.await.unwrap(), std::process::id());
}

#[tokio::test]
async fn events_arrive_on_the_event_channel() {
    let name = PipeName::random();
    let mut server = PipeServer::bind(&name).unwrap();
    let core = tokio::spawn(async move {
        let connection = server.accept().await.unwrap();
        let (mut reader, mut writer) = connection.into_split();
        let event = Envelope::new(
            RequestId::new(),
            Event::ListingLost {
                listing_id: 9,
                message: "gone".to_owned(),
            },
        );
        writer.send(&event).await.unwrap();
        // An event in between does not confuse the reply that follows.
        let request: Envelope<Request> =
            serde_json::from_slice(&reader.read_frame().await.unwrap()).unwrap();
        writer
            .send(&Envelope::new(request.id, Response::Ok))
            .await
            .unwrap();
        let _ = reader.read_frame().await;
    });

    let mut client = PipeClient::connect(&name, TIMEOUT).await.unwrap();
    let mut events = client.events().unwrap();
    assert!(client.events().is_none(), "the receiver is handed out once");
    let reply = client.request(Request::Ping).await.unwrap();
    assert_eq!(reply.body, Response::Ok);
    let event = events.recv().await.unwrap();
    assert!(matches!(
        event.body,
        Event::ListingLost { listing_id: 9, .. }
    ));
    drop(client);
    core.await.unwrap();
}

#[tokio::test]
async fn a_section_handle_from_the_core_is_taken_once() {
    let name = PipeName::random();
    let mut server = PipeServer::bind(&name).unwrap();
    let core = tokio::spawn(async move {
        let mut connection = server.accept().await.unwrap();
        let client_pid = connection.client_process_id().unwrap();
        let request: Envelope<Request> = connection.recv().await.unwrap();
        let section = SharedSection::create(4096).unwrap();
        section.map().unwrap().as_mut_slice()[..5].copy_from_slice(b"CBLS!");
        let handle = section.duplicate_for(client_pid).unwrap();
        let reply = Response::ListingOpened {
            listing_id: 1,
            section_handle: handle.0,
            section_size: 4096,
            entry_count: 0,
            generation: 1,
            elapsed_us: 1,
        };
        connection
            .send(&Envelope::new(request.id, reply))
            .await
            .unwrap();
        // The core closes its own handle; the client's copy keeps the section.
        drop(section);
        let _ = connection.read_frame().await;
    });

    let mut client = PipeClient::connect(&name, TIMEOUT).await.unwrap();
    let reply = client.request(Request::Ping).await.unwrap();
    let Response::ListingOpened { section_handle, .. } = reply.body else {
        panic!("expected listing_opened, got {:?}", reply.body);
    };
    let section = client.take_section(section_handle).unwrap();
    assert_eq!(&section.map_readonly().unwrap().as_slice()[..5], b"CBLS!");
    assert!(client.take_section(section_handle).is_err(), "taken twice");
    assert!(client.take_section(0x1234).is_err(), "never sent");
    drop(client);
    core.await.unwrap();
}

#[tokio::test]
async fn waiting_requests_fail_when_the_connection_ends() {
    let name = PipeName::random();
    let mut server = PipeServer::bind(&name).unwrap();
    let core = tokio::spawn(async move {
        let mut connection = server.accept().await.unwrap();
        let _request = connection.read_frame().await.unwrap();
        // Hang up without replying.
    });
    let mut client = PipeClient::connect(&name, TIMEOUT).await.unwrap();
    let result = client.request(Request::Ping).await;
    assert!(
        matches!(result, Err(IpcError::Closed | IpcError::Io(_))),
        "{result:?}"
    );
    let again = client.request(Request::Ping).await;
    assert!(again.is_err());
    core.await.unwrap();
}
