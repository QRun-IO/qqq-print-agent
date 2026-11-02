# Virtual file printer (development agent)

A small Node.js (24+, no dependencies) print agent for exercising a QQQ server with the
[printing QBit](https://github.com/QRun-IO/qbit-printing) end to end on macOS or Linux, without
Windows, a spooler or hardware. It speaks the same contract as the Windows service: printing
extension `>=2.0.0 <3.0.0`, wire v1, the authenticated `qqq-printing.v1` WebSocket (HELLO,
WELCOME, STATE_APPLIED, INVENTORY, HEARTBEAT, JOB_AVAILABLE, JOB_EVENT, COMMAND_RECEIPT) and
claims/downloads over HTTP(S). It "prints" by writing each verified document to a directory.

It is a development tool, not a product: it exposes one printer, handles one job at a time
(declared honestly in HELLO limits), keeps no journal beyond its device identity and revisions,
and does not qualify the Windows service. A written file is `SPOOLED` and then
`SPOOLER_COMPLETED`; nothing comes out of a printer.

```sh
# first run: enroll through the native "Enroll a print device" process and approve the printer
node tools/virtual-agent/virtual-agent.mjs --server http://localhost:8078 --enroll --approve
# later runs reuse the identity in ~/.qqq-print-agent/state.json; printed PDFs land in ~/Downloads
node tools/virtual-agent/virtual-agent.mjs --server http://localhost:8078
# or somewhere else
node tools/virtual-agent/virtual-agent.mjs --server http://localhost:8078 --out ./printed
```

| Option | Meaning |
|---|---|
| `--server <origin>` | QQQ server (`QQQ_PRINT_SERVER`); plain http only for loopback |
| `--out <dir>` | where printed documents go (`QQQ_PRINT_OUT`; default `~/Downloads`, as browser downloads and qqq exports); each job writes `<time>_<jobId>_<title>.pdf` (or `.zpl`) plus a `.json` receipt |
| `--state <file>` | device identity and applied revisions (default `~/.qqq-print-agent/state.json`, mode 0600; an identity an earlier version kept at `<out>/.agent-state.json` is still used) |
| `--enroll` | with no stored identity, run `printingEnrollDevice` for a code and enroll |
| `--approve` | approve the virtual printer with `printingEnableSelectedPrinters` (local testing only; approval is normally an administrator's decision) |
| `--name`, `--device-name`, `--printer-id`, `--formats PDF,ZPL`, `--facility` | inventory and enrollment details |
| `--once` | exit after the first printed job |

Sign-in follows the server's advertised authentication: MOCK and FULLY_ANONYMOUS use
`manageSession`; TABLE_BASED uses HTTP Basic from `QQQ_PRINT_USERNAME` / `QQQ_PRINT_PASSWORD`.
OAuth2 is not supported. Credentials, device tokens and enrollment codes are never logged.
Supported commands: `REFRESH_INVENTORY` (answers `INVENTORY_REFRESHED`); others are `UNSUPPORTED`.

Tests: `node --test tools/virtual-agent/test/virtual-agent.test.mjs`.
The scripts workflow runs these tests with Node 24. HTTP tests use disposable
loopback servers and synthetic cookies, without a QQQ deployment or printer.
Discovery accepts only canonical stable three-part printing versions in major 2.
Authenticated native HTTP requests remain on the configured origin; unsafe
cookie requests include its exact Origin, and redirects are refused.
