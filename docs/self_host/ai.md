# Buoy Money: AI providers

AI is optional. A self-hosted server starts with AI **off**: nothing is sent anywhere, AI screens
are hidden, and the background categorizer does nothing. You turn it on by choosing a provider.
The hosted app at buoy-ai.com runs the same code on Gemini, as it always has.

## Pick a provider

Set these in `selfhost/.env` (or as `Ai__...` environment variables), then restart the server.
The provider is read once at startup.

| `BUOY_AI_PROVIDER` | What it is | Needs |
|---|---|---|
| (blank) or `none` | AI off. The default on self-host. | nothing |
| `ollama` | A model running on your own computer. Free and private. | `BUOY_AI_MODEL`; Ollama installed |
| `openai` | OpenAI. Costs money; sends transaction text to OpenAI. | `BUOY_AI_MODEL`, `BUOY_AI_API_KEY` |
| `openrouter` | OpenRouter, many models behind one key. Costs money. | `BUOY_AI_MODEL`, `BUOY_AI_API_KEY` |
| `gemini-compat` | Google Gemini through its OpenAI-style address. | `BUOY_AI_MODEL`, the Gemini key |
| `custom` | Any server that speaks the OpenAI chat-completions protocol (LM Studio, vLLM, llama.cpp). | `BUOY_AI_BASE_URL`, `BUOY_AI_MODEL` |
| `gemini` | Google Gemini through its own SDK. What hosted runs. | `GOOGLE_API_KEY` or `Gemini__ApiKey` |
| `buoy-cloud` | AI bought from BuoyAI Cloud with a monthly allowance. No provider account of your own. | `BUOY_AI_CLOUD_URL`, `BUOY_AI_CLOUD_CREDENTIAL` (see below) |

Other settings, all optional:

| Setting | Default | What it does |
|---|---|---|
| `Ai__BaseUrl` | the preset's address | Where the server is. Ollama's is `http://localhost:11434/v1`. |
| `Ai__Models__categorization` | `Ai__Model` | A different model for one feature (`categorization`, `insights`, `memory-extraction`, `subscriptions`, `receipts`). |
| `Ai__EmbeddingModel` | none | Turns on similar-transaction and help search. For Ollama: `nomic-embed-text` or `embeddinggemma`. |
| `Ai__EmbeddingDimensions` | 768 | Must match the embedding model. A reply of another length is refused, never stored. |
| `Ai__ReasoningEffort` | not sent | `none` turns off "thinking" on models that think by default (Qwen3, DeepSeek-R1). |
| `Ai__TimeoutSeconds` | 120 | Per call. Raise it for a slow model. |
| `Ai__Temperature` | 0 | 0 keeps one-line answers repeatable. |
| `Ai__PriceInputPerMillion`, `Ai__PriceOutputPerMillion` | 0 | USD per million tokens, so the usage numbers mean something. Ollama is always 0. OpenRouter reports its own cost. |

Ollama in Docker: the app container reaches your computer at
`http://host.docker.internal:11434/v1` (the compose file maps that name on Linux too).

On Linux, Ollama listens only on `127.0.0.1` by default, and the container reaches your computer
over the Docker bridge, so the connection is refused until Ollama listens on all addresses:
set `OLLAMA_HOST=0.0.0.0` (with systemd: `sudo systemctl edit ollama`, add
`Environment="OLLAMA_HOST=0.0.0.0"` under `[Service]`, then restart Ollama). Docker Desktop on
Mac and Windows does not need this. If you open port 11434 to the network, firewall it.

In `selfhost/.env` these settings are `BUOY_AI_TIMEOUT_SECONDS`, `BUOY_AI_EMBEDDING_DIMENSIONS`,
`BUOY_AI_TEMPERATURE`, `BUOY_AI_PRICE_INPUT_PER_MILLION`, `BUOY_AI_PRICE_OUTPUT_PER_MILLION`,
and `BUOY_AI_MODEL_CATEGORIZATION`, `BUOY_AI_MODEL_INSIGHTS`, `BUOY_AI_MODEL_SUBSCRIPTIONS`
for per-feature models.

## BuoyAI Cloud AI (`buoy-cloud`)

The paid add-on for people who want AI without running a model or keeping an account with a provider. Each AI call goes to
BuoyAI Cloud's gateway with this server's pairing credential. The gateway picks the model, counts the cost against a monthly
allowance, and stops when the allowance is gone. Nothing from the Cloud code is part of this app: the server only knows an
address, a credential and the HTTP below.

Set `BUOY_AI_PROVIDER=buoy-cloud`, `BUOY_AI_CLOUD_URL` (the gateway's address) and `BUOY_AI_CLOUD_CREDENTIAL` (the pairing
credential; keep it out of logs and screenshots). Same limits as the other non-Gemini providers: categorization, subscription
wording, memory extraction and the recap work; chat with tools, agents and receipt scanning stay off.

* **One allowance for the whole server**, not per person. The usage screen (Settings > AI usage) shows it to every member.
* **When the allowance runs out** the call fails with a plain message and the app can offer pay-as-you-go.
* **Pay-as-you-go** is off by default and is the **admin's** switch, because it spends the owner's money. It has a cap the
  admin sets. Calls continue past the allowance until the cap, then stop. Only that overage is billed.
* **Alerts at 80 and 100 percent** of the allowance reach the owner by push and email, once per period. The percentage is also
  on the usage screen.
* **Fail closed.** If the gateway can't count a call (its meter or the pairing check is down), AI is paused rather than
  allowed through unmetered.

The gateway's HTTP, for anyone implementing or reviewing the client (`Authorization: Bearer <credential>` on all of it):

| Call | Purpose |
|---|---|
| `POST /v1/chat/completions` | An AI call in the OpenAI chat-completions shape. Headers `X-Buoy-Feature` (one of `chat`, `categorization`, `insights`, `agentic`, `memory-extraction`, `subscriptions`, `receipts`, `wrapped`) and `X-Request-Id`. The reply carries `usage` and a `buoy` block (`usedMicros`, `allowanceMicros`, `percent`, `state`, `callCostMicros`). |
| `GET /v1/usage` | Period start and end, allowance, used, percent, state (`ok`, `warning`, `over`), pay-as-you-go status, per-feature breakdown, projected spend. |
| `PUT /v1/usage/payg` | `{ "enabled": true, "capMicros": 5000000 }`. Money is always whole micro-dollars (1 USD = 1,000,000). |

Errors are `{ "error": { "code", "message", "details" } }`. `allowance_exhausted` and `payg_cap_reached` (HTTP 402) become a 402
from this server with `canEnablePayg`; everything else becomes a plain "AI is unavailable" message with the gateway's wording.

## Usage and the usage screen

Every AI call a person causes now writes a usage row (`AIUsageRecords`) with the provider's own input and output token counts,
the provider, and a flag saying whether the counts are a guess (`TokensEstimated`). Before, only chat and categorization wrote
rows, and the token total was split 70/30 into input and output, which mispriced every row. Now insights, receipt scanning, recap
wording, memory extraction, subscription wording and the agentic drill-down write rows too, under the feature names
`insights`, `receipts`, `memory-extraction`, `subscriptions`, `agentic`, `chat` and `categorization`. Thinking tokens are counted
as output, as Google bills them.

* **One price table** (`AiPricing`). An exact model name wins, then a dated or preview variant of a known model; an unknown Gemini
  model is counted at the dearest row of its family, never silently as an older cheaper model; a model that isn't Gemini is 0
  unless the provider reports its own cost (OpenRouter does) or you set `Ai__PriceInputPerMillion` and `Ai__PriceOutputPerMillion`.
* **The month is the billing period.** A subscriber's figures reset on their billing day, not on the 1st.
* **No caps on self-host.** The per-tier request and dollar limits belong to the hosted service. A self-hosted server only keeps
  statistics (with BuoyAI Cloud the gateway enforces the allowance).
* **Hosted fails closed:** if the usage figures can't be read, `CheckMonthlyLimitAsync` refuses instead of allowing. Set
  `Ai__Metering__FailClosed=false` to go back to the old behaviour.

The screen reads `GET /api/aiusage`; the admin's pay-as-you-go switch is `PUT /api/aiusage/payg` with `{ "enabled", "capMicros" }`.

## What works with which provider

This is what the code does today, and the Settings > AI provider screen shows the same list for
your server.

| Feature | Gemini | Any other provider |
|---|---|---|
| Transaction categorization | works | works |
| Subscription insights, monthly recap wording | works | works |
| Dashboard insights | works | works on 14B or larger with a 16k context window; limited on small local models |
| Similar-transaction and help search | works | works when `Ai__EmbeddingModel` is set |
| Chat that looks things up in your data | works | off (needs tool calling, Gemini only for now) |
| Insight drill-down, "let AI change my budget" | works | off (same reason) |
| Receipt and photo scanning | works | off (attachments are Gemini only for now) |

With AI off, or a feature off, the app hides that screen and the background jobs skip: nothing
is invented. Rows that AI would have categorized keep the bank's category or stay uncategorized
for you to file by hand.

### Ollama and context length

Ollama gives models a 4k-token window unless you raise it, and silently cuts off the rest of a
longer prompt. Categorization fits in 4k. Insights want at least 16k and chat with tools wants
64k. Set `OLLAMA_CONTEXT_LENGTH=16384` (or higher) where Ollama runs.

### Models that think

Some models (Qwen3 and later, DeepSeek-R1) write a long hidden reasoning before the answer. For a
one-line categorization that is minutes of waste. Set `BUOY_AI_REASONING_EFFORT=none`. On the
measured run below it cut one call from about 80 seconds to about 1.3 seconds.

## Check it works

Settings > AI provider shows everyone the list above. The admin also sees the connection details and a "Check the connection" button. It makes two small calls
with made-up data: can the server reach the model, and does the model answer the categorization
prompt in the format the app can read. Passing the second one is the real signal.

## Measure a model before you trust it

`backend/tools/BuoyAI.AiBench` scores a model on 384 made-up, labelled transactions using the
app's real categorization prompt and the app's real reply parser, so the score is what you would
get. It never runs by itself and costs nothing against a local model.

```bash
cd backend
dotnet run --project tools/BuoyAI.AiBench -- --list --provider ollama                 # what is installed
dotnet run --project tools/BuoyAI.AiBench -- --provider ollama --models qwen2.5:7b,llama3.2:3b \
    --reasoning-effort none --out results.json --md results.md
```

Useful options: `--rows 100` for a quick pass, `--runs 3` to measure how often a model gives the
same answer twice, `--baseline-accuracy 0.9` (the accuracy of your reference model on the same
set) to get a verdict, `--dry-run` to see the prompt, `--dump-dataset set.json` to read the data.
Cloud providers work too (`--provider openrouter --api-key ...`) and cost real money; nothing in
the repo runs them. `--provider fake` is a self-test of the tool and says nothing about any model.

The set has four buckets: plain merchants (messy bank strings such as `SQ *BLUE BOTTLE COFFEE
0412`, with a Plaid guess that is wrong one time in five), income, split merchants (Walmart as
groceries and as shopping, told apart only by amount and weekday through the history block in the
prompt), and the user's own categories (`C1001` form). Transfers and card payments are left out
because the app filters them before any model sees them.

Verdicts follow the bar proposed in the research: **recommended** at 98% valid replies and
accuracy within 5 points of the baseline; **usable** at 90% and within 12 points. Without a
baseline the tool reports format only.

### One measured run

Scored 2026-10-12 on the build machine (Windows, 16 logical CPUs, 32 GB, RTX 4070 SUPER 12 GB,
Ollama 0.30.4), 384 rows, one run, two calls at a time, thinking off. A single run on one machine
is a data point, not a ranking.

| Model | Valid replies | Accuracy | Plain | Income | Split merchants | Custom categories | p50 / p95 latency | Rows per minute |
|---|---|---|---|---|---|---|---|---|
| qwen3.5:9b | 97.9% | 81.2% | 88.4% | 100% | 66.2% | 51.3% | 1.3 s / 2.1 s | 83.8 |

What it shows: the format is mostly followed, but about 2% of replies are unreadable (for example
`13:Subscriptions:tags`, which drops the confidence). Split merchants are the hard case: the model
files a $9 Amazon charge and a Dillons fuel stop under the merchant's main category every time,
ignoring the history block. Custom categories are weak too (51%). Confidence is not well calibrated
(0.97 when right, 0.87 when wrong), so the review threshold catches little. A one-retry repair for
unreadable replies, and a structured-output variant of the prompt, are the next things to try.
Other model sizes were not run.
