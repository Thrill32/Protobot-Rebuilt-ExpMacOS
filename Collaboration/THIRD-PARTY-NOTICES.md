Collaboration source is covered by the repository's GPL-3.0 license in ../LICENSE.

Self-contained distributions include Microsoft .NET and ASP.NET Core, licensed under the MIT license with additional third-party notices. Distribute the SDK/runtime LICENSE.txt and ThirdPartyNotices.txt alongside each helper (as LICENSE-dotnet.txt and NOTICES-dotnet.txt), plus LICENSE-Protobot.txt. The release workflow copies those notices from the pinned SDK installation; do not omit them from desktop helper folders or CLI archives.

Durable hosting adds Microsoft.Data.Sqlite 10.0.14 (Microsoft/.NET Foundation, MIT) and SQLitePCLRaw 2.1.13 (Copyright 2014–2024 SourceGear, LLC, Apache-2.0), including its native e_sqlite3 library. The SQLite engine is public domain. Distribute `Microsoft.Data.Sqlite-LICENSE.txt` and `SQLitePCLRaw-LICENSE.txt` with every helper; the publish scripts copy them from `licenses/`. The server's development and per-runtime NuGet lock files pin the complete dependency graph; the publish scripts and CI enforce locked restore. The native library is part of each runtime-specific helper, not a Unity plug-in.

No SignalR, proprietary networking service or separately maintained client implementation is bundled. The Unity/shared client uses platform .NET APIs; SQLite is confined to the standalone helper. The production transport/network acceptance gates remain open.

Upstream notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT and https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.TXT.
