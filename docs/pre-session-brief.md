# Pre-Session Brief (read-only research)

The administrator workspace has a **Pre-Session** dialog backed by
`GET /api/market-analysis/pre-session?date=YYYY-MM-DD`. Nifty and Sensex have
separate immutable snapshots. The capture worker attempts an `Overnight` snapshot
from 08:45–08:48 IST and a `PreOpen` snapshot from 09:12–09:15 IST on weekdays.
If the app or database is unavailable during a slot, the slot stays missing: it
must not be backfilled with information acquired after the stated time. The
source's timestamp and the snapshot's capture timestamp are distinct.

Only existing, authorized stored data are read. The first version uses the last
recorded prior-session one-minute Groww index candles for close/high/low and the
last saved option-chain sample from that same prior session for a *sampled*
call/put OI concentration. OI is not called today's buildup. The current Groww
feed starts at 09:15, so there is no validated Indian pre-open price in this
version. GIFT Nifty, US/Asian markets, crude, yields, USD/INR, FII/DII flows,
and event-calendar data are displayed as unavailable because no authorized
timestamped providers are connected. They are not estimated, scraped, or
silently converted to directional signals. A later licensed/authorized feed
can add those inputs with source timestamps and freshness checks.

The report offers conditional breakout, breakdown, and inside-range scenarios
with invalidation levels, not a trade instruction. Without a validated pre-open
price its opening *bias* is `Unclear`. A separate, explicitly low-confidence
morning *judgement* is saved before trading: a strong prior-session body and
close near its high/low form a provisional continuation guess; otherwise it
is `Unclear`. A future verified indicative gap could inform that guess, but
the present feed has no such value. This simple hypothesis is not calibrated
and must not be treated as a trading signal.

The capture worker also attempts an immutable `Outcome` snapshot from
15:45–16:30 IST on weekdays. It records the observed open/high/low/close,
one-minute candle count, directional classification and whether the saved
morning guess was correct, incorrect, or unscored. The fixed classification
calls a session up/down when its open-to-close change is at least 40% of its
high–low range; otherwise it is mixed. Scoring requires at least 250 candles,
a first sample no later than 09:20, and a last sample no earlier than 15:20.
Missing morning capture, unclear guess or insufficient coverage is unscored.
The UI also shows a rolling 60-calendar-day scorecard for each market using
only scored directional guesses. Neither the morning record nor the outcome
is revised after capture; a missed slot is not backfilled. This is an
exploratory directional test, not a measure of trade profitability or a
guarantee of future performance.

Snapshots do not enter strategy, risk, size, execution, or order-routing
paths. No additional broker requests are made by this feature.

Before any input affects trading, validate it prospectively over enough sessions
to compare outcomes, data availability, and missed opportunities against the
existing system. Feed licensing and market-data usage rights must be checked
before connecting or redistributing external data.
