# Working on this ENCY extension

<!-- Generated from guides/_index.json in EncySoftware/ency-extension-mcp.
     Edit the guides there and run tools/sync-rules.ps1 - changes made here are overwritten. -->

This repo is one extension for **ENCY 3**. GitHub Actions builds it and the ENCY Extension Store
packs and publishes it when you push a version tag.

**Write for ENCY 3, not ENCY 2.** The SDK is pinned in `src/EncyExtension.csproj` as
`EncySoftware.CAMAPI.Sdk.Net` 3.0.8 - the 3.x line; a newer 3.x on the public feed is fine to
move to. Do not "fix" anything by moving to a 2.x version: 2.x is the previous generation of the
product, and an extension built against it is an extension for the old ENCY. (An assistant that read
the old instructions spent an hour writing for ENCY 2 - hence this paragraph.)

**The API reference lives outside this repo.** Three places, three different questions:

- [CAM API reference](https://docs.encycam.com/CAMAPI/3/en/) - every interface, property and method.
  Go here to answer "what can I actually call".
- [Lessons](https://docs.encycam.com/CAMAPI/3/en/src/Lessons/Main.html) - the same API taught in
  order, starting from a first extension. Go here when the reference tells you what exists but not
  where to begin.
- [cam-api-examples/docs](https://github.com/EncySoftware/cam-api-examples/tree/v3/main/docs) - a
  worked example of every extension kind, with the code that compiles.

The guides below are the short path for ONE kind; those three are the whole picture, and they are
where to look when an interface here is not enough.

**If you write a PowerShell script for the author, run it as**
`powershell -ExecutionPolicy Bypass -NoProfile -File <script.ps1>`. A client Windows blocks `.ps1`
by default ("running scripts is disabled on this system"), and a first-time author cannot tell that
error from a broken script. Prefer no script at all: publishing here needs none.

**Decide which entry point you need BEFORE writing extension code, then read its guide.** Each guide
gives the exact interface, the `*.settings.json` key, a compiling skeleton and the traps. The guides
are plain markdown under `.cursor/rules/` - open them directly. If the `ency-extension-store` MCP
server is connected, `get_extension_guide` serves the same text (`type=list` lists every kind).

| What the extension should do | Kind | Guide |
|---|---|---|
| Add a button to the ENCY utilities menu or toolbar; runs on click with the full application context | `utility` | `.cursor/rules/type-utility.mdc` |
| Run code once when ENCY starts and once when it shuts down; no UI of its own | `global` | `.cursor/rules/type-global.mdc` |
| Intercept or wrap how another utility is executed - custom file selector, pre/post processing | `utility_runner` | `.cursor/rules/type-utility-runner.mdc` |
| Add items to the right-click menu of a technology operation in the operations tree | `operation_popup` | `.cursor/rules/type-operation-popup.mdc` |
| Add items to the right-click menu of a node in the 3D geometry model tree | `geom_model_node_popup` | `.cursor/rules/type-geom-node-popup.mdc` |
| Implement your own toolpath calculation algorithm for one or more operation types | `operation_solver` | `.cursor/rules/type-operation-solver.mdc` |
| Transform the calculated CLD toolpath data before it reaches the next pipeline stage | `cldata_converter` | `.cursor/rules/type-cldata-converter.mdc` |
| Integrate ENCY with a PLM system: browse items, download projects, upload results | `plm` | `.cursor/rules/type-plm.mdc` |

Two guides apply to every change:

- `.cursor/rules/ency-extension.mdc` - repo anatomy: the `CAMAPI.ExtensionFactory` contract,
  matching ids between `*.settings.json` and the factory, `package.info.json`, how to build for
  packing.
- `.cursor/rules/ency-cookbook.mdc` - COM lifetime (`ComWrapper`), errors through
  `TResultStatus`, asking the user for parameters, windows and STA rules.

## Publishing

**Starting from nothing?** `create_extension_folder(name, dir)` from the same MCP server makes
the project from the template on this machine - no GitHub account, no git; write the code in its
`src/`, then publish it as below.

**Preferred: `publish_folder(name, folder)` from the `ency-extension-store` MCP server** - no git,
no gh. The store creates the repository in the author's GitHub account, commits this folder into
`src/`, runs the build and returns the result: the version and the card link, or the failing step
with its log (fix the code, call it again). The author is needed twice, in the browser only - the
store sign-in and, once, the app's consent page - and the tool opens both itself. Server not connected? Install it once, no questions asked: `dotnet tool install -g EncySoftware.ExtensionStoreMcp`. Run that yourself - it asks nothing - rather than handing it to the author, then ask the author to
restart the editor: this repository carries `.mcp.json` and `.cursor/mcp.json` that register the server
(a folder made before they existed: `ency-extension-mcp setup`, also yours to run). The server is connected but has no `publish_folder`? The tool is old - update it yourself, it asks
nothing: `dotnet tool update -g EncySoftware.ExtensionStoreMcp`, then ask the author to restart the editor. The same route from a terminal: `ency-extension-mcp publish-folder <Name> <folder>`.

**Never run `gh auth login` for the author** - it stops on a Y/n prompt that swallows the next
pasted command. Without the tool, the same route is the store page: https://apps.encycam.com/publish
-> **Code in a folder** -> pick this project's folder -> **Upload and publish**.

From a terminal where git and gh are already set up, a version tag does the same:

```bash
git tag v1.2.3 && git push --tags
```

Actions builds the project, the store packs the ENCY-format package and publishes it. A brand new
extension waits for a store moderator (its direct card link works immediately); new versions of an
approved extension go live at once.