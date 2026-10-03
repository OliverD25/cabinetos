# quickview-fixture

The test viewer of Quick View ([ADR 0023](../../../../docs/decisions/0023-quick-view-viewer-contract.md),
"The fixture viewer"). It is a Tool Extension that claims `*.qvtest` and
`*.png` in its `quickView` block and opens nothing in a pane (`accepts` is
empty). The core's tests install it from a local index and see the Quick
View table change; the window's tests use it to drive the panel.

In the panel, `quickview.html` reads the file from the `url` of
`quickview-show`:

| A `.qvtest` file's first line | What the page does |
|---|---|
| `shown` | `quickview-shown` after two frames |
| `shown-keys left right` | `quickview-shown` asking for the keys after the word |
| `failed` | `quickview-failed` with the reason `unsupported` |
| `hang` | nothing after `ready` |
| `crash` | an endless loop |
| `slow 4000` | `quickview-shown` after the number of milliseconds |

A PNG is shown as an image and reported once it is painted, with its size
as `details`. Nothing of the viewer pack is needed.
