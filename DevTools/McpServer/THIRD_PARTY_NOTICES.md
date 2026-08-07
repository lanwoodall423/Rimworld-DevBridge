# MCP Server Notices

`RimWorldDevBridge.McpServer` is an optional local adapter. It is not part of the
eleven-file RimWorld mod package and does not redistribute RimWorld, Unity, Harmony,
owner-mod, save, or user data.

The self-contained artifact includes code from `ModelContextProtocol` 2.1.0 and its
transitive dependencies. `ModelContextProtocol` is maintained by the official MCP C#
SDK project and is licensed under Apache-2.0. Microsoft.Extensions.Hosting and its
transitive dependencies are Microsoft libraries distributed under their respective
MIT or Apache-2.0 notices. Obtain exact dependency license texts from the NuGet
package metadata used by the locked restore before redistributing the optional artifact.

The optional artifact is reproducible with
`DevTools/Build-RimWorldDevBridgeMcpServer.ps1`; its output must contain only the
self-contained executable and this notice file.
