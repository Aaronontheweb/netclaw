// -----------------------------------------------------------------------
// <copyright file="SessionHubJsonContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Serialization;
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Gateway;

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(SessionEnsureResultDto))]
[JsonSerializable(typeof(SessionOutputDto))]
internal sealed partial class SessionHubJsonContext : JsonSerializerContext;
