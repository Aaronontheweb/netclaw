# chat-inline.tape — drive the primary-buffer chat view through a real turn.

Output "/tmp/tape-chat-inline.gif"

Type "mkdir -p $NETCLAW_HOME/config"
Enter
Type "provider=ollama endpoint=http://localhost:11434 model=qwen2:0.5b role=smoke jq -n '{configVersion:1,Providers:{($ENV.provider):{Type:$ENV.provider,Endpoint:$ENV.endpoint}},Models:{Definitions:{($ENV.role):{Provider:$ENV.provider,ModelId:$ENV.model}},Roles:{Main:$ENV.role}}}' > $NETCLAW_HOME/config/netclaw.json"
Enter
Wait+Screen@5s /TAPE\$/

Type "netclaw daemon start"
Enter
Wait+Screen@30s /Daemon started|already running/
Wait+Screen@5s /TAPE\$/
Type "until netclaw status >/dev/null 2>&1; do sleep 1; done; echo DAEMON_READY"
Enter
Wait+Screen@90s /DAEMON_READY/
Wait+Screen@5s /TAPE\$/

Type "netclaw chat"
Enter
Wait+Screen@60s /Composer/

Type "Say hello"
Shift+Enter
Type "in one word"
Enter
Wait+Screen@15s /Generating/
Wait+Screen@120s /USAGE/
Wait+Screen@20s /Ready/
Wait+Screen@20s /Composer/

Up
Escape
Escape
Wait+Screen@10s /Input cleared/

Ctrl+O
Wait+Screen@10s /INSPECTOR/
Escape
Ctrl+Q
Sleep 1s
Wait+Screen@10s /TAPE\$/

Type "echo CHAT_INLINE_EXIT=$?"
Enter
Wait+Screen@5s /CHAT_INLINE_EXIT=0/
Type "exit"
Enter
