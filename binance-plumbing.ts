// Binance Spot testnet — execution plumbing check (NOT an HFT latency test; ccxt adds ms overhead).
// What it actually proves: auth + clock sync, an own clientOrderId state machine, order round-trip
// latency, and that the three failure modes an interviewer probes are handled, not hoped away.
//   1. -1021 timestamp/recvWindow   2. cancel of an already-gone order   3. rate-limit backoff
// Orders are far-below-market BUY limits so they rest and never fill — we measure the round trip,
// we don't take a position.
import ccxt from 'ccxt'

const KEY = process.env.BINANCE_TESTNET_KEY
const SECRET = process.env.BINANCE_TESTNET_SECRET
if (!KEY || !SECRET) throw new Error('BINANCE_TESTNET_KEY / _SECRET missing — source ~/.claude/.secrets')

const SYMBOL = 'BTC/USDT'
const REPS = Number(process.env.REPS ?? 20)

// Own order lifecycle, keyed by our clientOrderId — the state machine the interview asks for.
type State = 'NEW' | 'SENT' | 'ACKED' | 'FILLED' | 'CANCELED' | 'REJECTED'
interface Order { clientOrderId: string; state: State; exchangeId?: string; error?: string }
const book = new Map<string, Order>()
let seq = 0
const nextClientId = () => `plumb-${Date.now()}-${seq++}` // unique per order; Binance caps clientOrderId length, this fits

const ex = new ccxt.binance({
  apiKey: KEY,
  secret: SECRET,
  options: { defaultType: 'spot', adjustForTimeDifference: true }, // adjustForTimeDifference = the -1021 fix, done up front
})
ex.setSandboxMode(true) // -> testnet.binance.vision

const pct = (xs: number[], p: number) => {
  if (!xs.length) return NaN
  const s = [...xs].sort((a, b) => a - b)
  return s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))]
}

// Classify a ccxt error into the failure mode it is, so each is handled explicitly, not swallowed.
function classify(e: any): string {
  const m = String(e?.message ?? e)
  if (e instanceof ccxt.RateLimitExceeded || /-1003|too many requests/i.test(m)) return 'RATE_LIMIT'
  if (e instanceof ccxt.InvalidNonce || /-1021|recvWindow|timestamp/i.test(m)) return 'CLOCK_SKEW'
  if (e instanceof ccxt.OrderNotFound || /-2011|unknown order/i.test(m)) return 'ORDER_GONE'
  if (e instanceof ccxt.InsufficientFunds) return 'NO_FUNDS'
  return 'OTHER'
}

async function main() {
  console.log(`ccxt ${ccxt.version} · Binance spot testnet · ${REPS} place/cancel reps\n`)
  await ex.loadMarkets()

  // Prove clock awareness explicitly (the -1021 root cause we already hit in the shell check).
  const before = Date.now()
  const srv = await ex.fetchTime()
  const offset = srv - Math.round((before + Date.now()) / 2)
  console.log(`clock: local vs server offset ≈ ${offset}ms (ccxt.adjustForTimeDifference handles it)`)

  const mkt = ex.market(SYMBOL)
  const ticker = await ex.fetchTicker(SYMBOL)
  const last = ticker.last ?? ticker.close!
  // rest 10% below market: never fills, and stays inside Binance's PERCENT_PRICE filter.
  const price = Number(ex.priceToPrecision(SYMBOL, last * 0.9))
  const minCost = (mkt.limits?.cost?.min ?? 10) * 1.5
  const minAmt = mkt.limits?.amount?.min ?? 0
  const amount = Number(ex.amountToPrecision(SYMBOL, Math.max(minAmt, minCost / price)))
  console.log(`resting BUY ${amount} ${SYMBOL} @ ${price} (last ${last}) — notional ≈ ${(amount * price).toFixed(2)} USDT\n`)

  const placeMs: number[] = []
  const cancelMs: number[] = []
  const errors: Record<string, number> = {}
  let filledUnexpectedly = 0

  for (let i = 0; i < REPS; i++) {
    const cid = nextClientId()
    const o: Order = { clientOrderId: cid, state: 'NEW' }
    book.set(cid, o)
    try {
      o.state = 'SENT'
      const t0 = performance.now()
      const created = await ex.createOrder(SYMBOL, 'limit', 'buy', amount, price, { newClientOrderId: cid })
      placeMs.push(performance.now() - t0)
      o.exchangeId = created.id
      o.state = created.status === 'closed' ? 'FILLED' : 'ACKED'
      if (o.state === 'FILLED') filledUnexpectedly++

      const t1 = performance.now()
      await ex.cancelOrder(created.id, SYMBOL)
      cancelMs.push(performance.now() - t1)
      o.state = 'CANCELED'
    } catch (e: any) {
      const kind = classify(e)
      errors[kind] = (errors[kind] ?? 0) + 1
      o.state = 'REJECTED'
      o.error = kind
      if (kind === 'RATE_LIMIT') await Bun.sleep(1000 + 200 * i) // real backoff, not a silent retry
      else if (kind === 'CLOCK_SKEW') { await ex.loadTimeDifference(); } // resync then move on
    }
  }

  // Deliberately trigger failure mode #2: cancel an order that's already gone -> must be handled, not thrown.
  let orderGoneHandled = false
  const done = [...book.values()].find(o => o.state === 'CANCELED' && o.exchangeId)
  if (done) {
    try { await ex.cancelOrder(done.exchangeId!, SYMBOL) }
    catch (e) { orderGoneHandled = classify(e) === 'ORDER_GONE' }
  }

  const states = [...book.values()].reduce<Record<string, number>>((a, o) => (a[o.state] = (a[o.state] ?? 0) + 1, a), {})
  console.log('state machine:', states)
  console.log(`place  latency ms  p50 ${pct(placeMs, 50)?.toFixed(0)}  p95 ${pct(placeMs, 95)?.toFixed(0)}  p99 ${pct(placeMs, 99)?.toFixed(0)}  (n=${placeMs.length})`)
  console.log(`cancel latency ms  p50 ${pct(cancelMs, 50)?.toFixed(0)}  p95 ${pct(cancelMs, 95)?.toFixed(0)}  p99 ${pct(cancelMs, 99)?.toFixed(0)}  (n=${cancelMs.length})`)
  console.log('errors handled:', Object.keys(errors).length ? errors : 'none')
  console.log(`cancel-already-gone (-2011) handled: ${orderGoneHandled ? 'yes ✓' : 'not exercised'}`)
  if (filledUnexpectedly) console.log(`⚠️  ${filledUnexpectedly} orders filled (price not far enough below market)`)

  const ok = states['CANCELED'] > 0 && !states['NEW'] && !states['SENT']
  console.log(ok ? '\n✓ plumbing OK — orders round-tripped NEW→SENT→ACKED→CANCELED' : '\n✗ plumbing check failed')
  process.exit(ok ? 0 : 1)
}

main().catch(e => { console.error('fatal:', e?.message ?? e); process.exit(1) })
