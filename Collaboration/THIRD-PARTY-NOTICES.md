Collaboration source is covered by the repository's GPL-3.0 license in ../LICENSE.

Self-contained distributions include Microsoft .NET and ASP.NET Core, licensed under the MIT license with additional third-party notices. Distribute the SDK/runtime LICENSE.txt and ThirdPartyNotices.txt alongside each helper (as LICENSE-dotnet.txt and NOTICES-dotnet.txt), plus LICENSE-Protobot.txt. The release workflow copies those notices from the pinned SDK installation; do not omit them from desktop helper folders or CLI archives.

No SignalR, SQLite, proprietary networking service or separately maintained client implementation is bundled by this development alpha. The shared package uses platform .NET APIs. Recovery storage and the production transport decision remain later release gates.

Upstream notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT and https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.TXT.
