# Buoy Money: tools for AI assistants (Open WebUI, Claude)

A self-hosted server can offer its read-only money tools to an AI assistant you already run, such as
Open WebUI. The assistant can then answer "what did I spend on groceries last month?" from your own data.
The tools are the same ones BuoyAI Connect gives Claude and ChatGPT on the hosted app: budgets,
transactions, accounts, cash flow, savings goals and subscriptions, plus "remember" for notes. None of
them move money or change a budget.

## Turn it on

In `selfhost/.env`:

```
BUOY_MCP_ENABLED=true
BUOY_ORIGIN=https://buoy.example.ts.net   # the address the assistant will use
```

Restart with `docker compose up -d`. The tools are served at `<BUOY_ORIGIN>/mcp`.

## Make an access token

In the app, open **Settings, Connected apps, Access tokens**, choose **New token**, and name it after
the tool ("Open WebUI"). Copy the token now: it is shown once. A token works for a year, only on `/mcp`,
only for your own account, and stops working the moment you revoke it on the same screen.

## Add it to Open WebUI

Admin Panel, **Settings, External Tools**, add a connection:

| Field | Value |
|---|---|
| Type | MCP (Streamable HTTP) |
| URL | `<BUOY_ORIGIN>/mcp` |
| Auth | Bearer, with the token |

Then turn the tool on for a chat (the tools button under the message box) and ask a money question.
Pick a model that handles tool calls well; small local models often ignore tools.

Open WebUI must be able to reach the address. If both run on your tailnet, use the Tailscale name.

## Ask Buoy's own chat through Open WebUI

The other direction also works: Buoy's in-app AI can use the models behind Open WebUI. Make an API key in
Open WebUI (Settings, Account, API keys), then in `selfhost/.env`:

```
BUOY_AI_PROVIDER=custom
BUOY_AI_BASE_URL=https://openwebui.example.ts.net/api
BUOY_AI_API_KEY=sk-...
BUOY_AI_MODEL=qwen2.5:7b
```

See [ai.md](ai.md) for model choice and timeouts.
