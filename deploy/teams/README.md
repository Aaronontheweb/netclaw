# Microsoft Teams app package

This directory contains the source for the Netclaw Microsoft Teams app package.

The package grants personal, team, and groupchat bot scopes. It requests the
team-scoped RSC permission `ChannelMessage.Read.Group` and the chat-scoped RSC
permission `ChatMessage.Read.Chat`. These permissions support channel-thread
replies and Group Chat message delivery for the installed app.

The manifest also declares file support for the bot. Netclaw still keeps file
access disabled by default and enables its bounded attachment path only when
`Teams.AllowAttachments` is true. The package requests no tab, call, video, or
message-write permission.

The channel RSC permission delivers standard channel messages to the bot
endpoint for the installed team. The chat RSC permission delivers Group Chat
messages for the installed app. Netclaw admits an unmentioned channel message
only when it is from the same approved human in a root that they established
with a genuine bot mention. It discards all other unmentioned messages before a
session or model turn.

When you install or upgrade the package, an allowed team owner or team member
must approve `ChannelMessage.Read.Group`. A member of each chat must approve
`ChatMessage.Read.Chat`. Tenant policy can limit these approvals. Group Chat
ingress also requires `Teams.AllowGroupChats` and an explicit canonical ID in
`Teams.AllowedGroupChatIds`. Attachments require `Teams.AllowAttachments`.

## Build the package

Use public HTTPS pages for your privacy policy and terms of use.

```powershell
$BuildPackage = @{
    AppId = '00000000-0000-0000-0000-000000000000'
    DeveloperName = 'Example Operator'
    PrivacyUrl = 'https://example.com/privacy'
    TermsOfUseUrl = 'https://example.com/terms'
    OutputPath = './artifacts/netclaw-teams.zip'
    Version = '1.0.0'
    Verbose = $true
}

./build-package.ps1 @BuildPackage
```

The developer name has a 32-character limit. Increase the semantic version for
each package update. An allowed team owner or team member must approve the team
RSC permission. A member of each chat must approve the chat RSC permission.

The ZIP file contains these three files at its root:

- `manifest.json`
- `color.png`
- `outline.png`

Do not commit an operator-specific package. The generated manifest contains
your app registration ID and your policy URLs.

See [the Microsoft Teams runbook](../../docs/integrations/microsoft-teams-channel.md)
for registration, deployment, health, rotation, and rollback procedures.
