import { useCallback, useEffect, useState } from 'react'
import {
  api,
  ApiError,
  type Allocation,
  type AllocationOutcome,
  type IncomingTransaction,
  type MatchingCandidates,
  type PaymentChange,
  type PaymentRequest,
  type PaymentSummary,
} from '../api'
import { ErrorText } from '../components'
import { formatDate, formatMinorUnits, toMinorUnits, useAction, useIdempotencyKey, words } from '../util'
import { explain, ORIGIN_LABELS } from './matchingText'

// Feature 9: manual matching. Nothing here edits bank facts or a paid total: every change is an
// allocation, a revocation, or a handling decision, each with a reason and the versions shown.

export function HandlingBadge({ transaction: t }: { transaction: IncomingTransaction }) {
  const tone = t.handling_state === 'allocated' ? 'active' : t.handling_state === 'unallocated' ? 'pending' : 'archived'
  return <span className={`badge badge-${tone}`}>{words(t.handling_state)}</span>
}

export function PaymentStatusBadge({ status }: { status: PaymentRequest['status'] }) {
  return <span className={`badge badge-${status}`}>{words(status)}</span>
}

export function SummaryText({ summary: s, currency }: { summary: PaymentSummary; currency: string }) {
  return (
    <>
      <PaymentStatusBadge status={s.status} /> paid {formatMinorUnits(s.paid_amount, currency)}
      {s.remaining_amount !== null && s.remaining_amount > 0 && `, ${formatMinorUnits(s.remaining_amount, currency)} remaining`}
      {s.overpaid_amount > 0 && `, ${formatMinorUnits(s.overpaid_amount, currency)} overpaid`}
    </>
  )
}

/** A reason prompt that sends a command with one idempotency key per submission. */
function ReasonForm({
  label,
  placeholder,
  danger,
  busy,
  onSubmit,
}: {
  label: string
  placeholder: string
  danger?: boolean
  busy: boolean
  onSubmit: (reason: string, key: string) => Promise<boolean>
}) {
  const [reason, setReason] = useState('')
  const key = useIdempotencyKey()
  return (
    <form
      className="row"
      onSubmit={(e) => {
        e.preventDefault()
        void onSubmit(reason.trim(), key.current()).then((done) => {
          if (done) {
            key.reset()
            setReason('')
          }
        })
      }}
    >
      <input
        placeholder={placeholder}
        value={reason}
        onChange={(e) => {
          key.reset()
          setReason(e.target.value)
        }}
        required
      />
      <button type="submit" className={danger ? 'danger' : 'secondary'} disabled={busy || reason.trim() === ''}>
        {label}
      </button>
    </form>
  )
}

/**
 * Matching of one transaction: the automatic result, its allocation, and for an unallocated
 * receipt the candidates, a manual search, and allocation with a preview of the result.
 */
export function MatchingPanel({
  prefix,
  transaction: t,
  accountNumber,
  readOnly,
  onChanged,
}: {
  prefix: string
  transaction: IncomingTransaction
  accountNumber: string | null
  readOnly: boolean
  onChanged: (t: IncomingTransaction) => void
}) {
  const path = `${prefix}/transactions/${t.id}`
  const m = t.matching
  const [candidates, setCandidates] = useState<MatchingCandidates['candidates'] | null>(null)
  const [search, setSearch] = useState({ external_id: '', variable_symbol: '', amount: '' })
  const [chosen, setChosen] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const { busy, error, run, setError } = useAction()

  const loadCandidates = useCallback(
    async (query?: typeof search) => {
      const params = new URLSearchParams()
      if (query?.external_id.trim()) params.set('external_id', query.external_id.trim())
      if (query?.variable_symbol.trim()) params.set('variable_symbol', query.variable_symbol.trim())
      if (query?.amount.trim()) {
        const minor = toMinorUnits(query.amount)
        if (minor === null) throw new Error('Enter the amount in CZK, e.g. 450 or 450.50.')
        params.set('amount', String(minor))
      }
      const result = await api<MatchingCandidates>('GET', `${path}/matching-candidates${params.size ? `?${params}` : ''}`)
      setCandidates(result.candidates)
    },
    [path],
  )

  const canAct = !readOnly && t.handling_state === 'unallocated'
  useEffect(() => {
    if (canAct) void run(() => loadCandidates())
  }, [canAct, loadCandidates, run])

  /** Sends a handling command; on a stale version reloads the transaction instead of overwriting. */
  const command = async (suffix: string, reason: string, key: string, message: string) => {
    let done = false
    await run(async () => {
      try {
        onChanged(await api<IncomingTransaction>('POST', `${path}/${suffix}`, { expected_version: t.version, reason }, { idempotencyKey: key }))
        setNotice(message)
        done = true
      } catch (e) {
        if (e instanceof ApiError && e.status === 412) {
          onChanged(await api<IncomingTransaction>('GET', path))
        }
        throw e
      }
    })
    return done
  }

  if (!m) return null
  const selected = candidates?.find((c) => c.payment_request.id === chosen) ?? null

  return (
    <div className="forwarding">
      <h3>Matching</h3>
      <dl>
        <dt>Handling</dt>
        <dd>
          <HandlingBadge transaction={t} />
          {m.handling_reason && <span className="muted"> {m.handling_reason}</span>}
          {m.handled_by && <span className="muted"> ({m.handled_by}, {formatDate(m.handled_at)})</span>}
        </dd>
        <dt>Automatic matching</dt>
        <dd>
          {explain(m.result)}
          {m.checked_at && <span className="muted"> · checked {formatDate(m.checked_at)}</span>}
          {m.auto_matching_blocked && <span className="muted"> · switched off ({words(m.auto_matching_block_reason)})</span>}
        </dd>
        {m.manually_confirmed && (
          <>
            <dt>Confirmation</dt>
            <dd>Confirmed by a person in a review (manual confirmation). The email's authentication results are unchanged.</dd>
          </>
        )}
        {m.canonical_transaction_id && (
          <>
            <dt>Copy of</dt>
            <dd>
              <code>{m.canonical_transaction_id}</code>
            </dd>
          </>
        )}
        {m.open_review_case_ids.length > 0 && (
          <>
            <dt>Open review</dt>
            <dd>
              {m.open_review_case_ids.map((id) => (
                <code key={id}>{id} </code>
              ))}
              <span className="muted"> Resolve it under Review before allocating.</span>
            </dd>
          </>
        )}
        {m.allocation && (
          <>
            <dt>Allocated to</dt>
            <dd>
              <code>{m.allocation.payment_request_id}</code> {words(m.allocation.method)} by {m.allocation.created_by},{' '}
              {formatDate(m.allocation.created_at)}
              {m.allocation.reason && `: ${m.allocation.reason}`}
              <span className="muted"> Revoke it in the payment request's detail.</span>
            </dd>
          </>
        )}
        {m.manual_block && t.handling_state === 'unallocated' && (
          <>
            <dt>Cannot allocate</dt>
            <dd>{explain(m.manual_block)}</dd>
          </>
        )}
      </dl>
      {notice && <p className="notice">{notice}</p>}
      <ErrorText error={error} />

      {canAct && (
        <>
          <h4>Payment requests that may fit</h4>
          <form
            className="row"
            onSubmit={(e) => {
              e.preventDefault()
              setChosen(null)
              void run(() => loadCandidates(search))
            }}
          >
            <input placeholder="external_id" value={search.external_id} onChange={(e) => setSearch({ ...search, external_id: e.target.value })} />
            <input
              placeholder="Variable symbol"
              inputMode="numeric"
              value={search.variable_symbol}
              onChange={(e) => setSearch({ ...search, variable_symbol: e.target.value })}
            />
            <input placeholder="Amount (CZK)" inputMode="decimal" value={search.amount} onChange={(e) => setSearch({ ...search, amount: e.target.value })} />
            <button type="submit" className="secondary" disabled={busy}>
              Search
            </button>
            <button type="button" className="secondary" disabled={busy} onClick={() => void run(() => loadCandidates())}>
              Suggestions
            </button>
          </form>
          {candidates?.length === 0 && <p className="muted">No payment request of this account and currency fits.</p>}
          {candidates && candidates.length > 0 && (
            <table>
              <thead>
                <tr>
                  <th>external_id</th>
                  <th>VS</th>
                  <th>Expected</th>
                  <th>Now</th>
                  <th>Why</th>
                  <th>After allocating</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {candidates.map((c) => (
                  <tr key={c.payment_request.id}>
                    <td>
                      <code>{c.payment_request.external_id}</code>
                    </td>
                    <td>{c.payment_request.variable_symbol}</td>
                    <td>{formatMinorUnits(c.payment_request.amount, c.payment_request.currency)}</td>
                    <td>
                      <PaymentStatusBadge status={c.payment_request.status} />
                    </td>
                    <td>{c.reasons.map(words).join(', ')}</td>
                    <td>{c.proposed ? <SummaryText summary={c.proposed} currency={c.payment_request.currency} /> : '—'}</td>
                    <td>
                      {c.block ? (
                        <span className="muted">{explain(c.block)}</span>
                      ) : (
                        <button className="secondary" onClick={() => setChosen(chosen === c.payment_request.id ? null : c.payment_request.id)}>
                          {chosen === c.payment_request.id ? 'Close' : 'Allocate…'}
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {selected && m.allocatable && (
            <AllocateForm
              prefix={prefix}
              transaction={t}
              accountNumber={accountNumber}
              candidate={selected}
              onDone={(outcome) => {
                setChosen(null)
                setNotice(`Allocated to ${outcome.payment_request.external_id}: now ${words(outcome.payment_request.status)}.`)
                onChanged(outcome.transaction)
              }}
              onStale={() => void run(async () => onChanged(await api<IncomingTransaction>('GET', path)))}
              setError={setError}
            />
          )}

          <h4>Other actions</h4>
          {m.manual_block === null && (
            <ReasonForm
              label="Ignore"
              placeholder="Why it is unrelated, e.g. a private transfer"
              busy={busy}
              onSubmit={(reason, key) => command('ignore', reason, key, 'Ignored; it will not be matched.')}
            />
          )}
          {m.auto_matching_blocked && (
            <ReasonForm
              label="Enable automatic matching"
              placeholder="Why automatic matching may try again"
              busy={busy}
              onSubmit={(reason, key) => command('enable-auto-matching', reason, key, 'Automatic matching enabled; a new attempt is queued.')}
            />
          )}
          <button
            className="secondary"
            disabled={busy}
            onClick={() =>
              void run(async () => {
                await api('POST', `${path}/match`)
                setNotice('A matching attempt is queued. Reviews and switched-off matching still apply. Reopen the transaction to see the result.')
              })
            }
          >
            Match again
          </button>
        </>
      )}

      {!readOnly && t.handling_state === 'ignored' && (
        <ReasonForm
          label="Restore"
          placeholder="Why it should be handled again"
          busy={busy}
          onSubmit={(reason, key) => command('restore', reason, key, 'Restored. Allocate it by hand, or enable automatic matching.')}
        />
      )}
    </div>
  )
}

function AllocateForm({
  prefix,
  transaction: t,
  accountNumber,
  candidate: c,
  onDone,
  onStale,
  setError,
}: {
  prefix: string
  transaction: IncomingTransaction
  accountNumber: string | null
  candidate: MatchingCandidates['candidates'][number]
  onDone: (outcome: AllocationOutcome) => void
  onStale: () => void
  setError: (e: unknown) => void
}) {
  const r = c.payment_request
  const [reason, setReason] = useState('')
  const key = useIdempotencyKey()
  const { busy, error, run } = useAction()

  return (
    <form
      className="stub-banner"
      onSubmit={(e) => {
        e.preventDefault()
        setError(null)
        void run(async () => {
          try {
            const outcome = await api<AllocationOutcome>(
              'POST',
              `${prefix}/transactions/${t.id}/allocations`,
              { payment_request_id: r.id, expected_transaction_version: t.version, expected_request_version: r.version, reason: reason.trim() },
              { idempotencyKey: key.current() },
            )
            key.reset()
            onDone(outcome)
          } catch (err) {
            if (err instanceof ApiError && err.status === 412) onStale()
            throw err
          }
        })
      }}
    >
      <strong>
        Allocate {formatMinorUnits(t.amount, t.currency)} to {r.external_id}
      </strong>
      <dl>
        <dt>Account</dt>
        <dd>
          <code>{accountNumber ?? t.bank_connection_id}</code> (request: <code>{r.account_number}</code>)
        </dd>
        <dt>Received</dt>
        <dd>
          {ORIGIN_LABELS[t.origin_path]}, {t.evidence} evidence
          {t.matching?.manually_confirmed && ', confirmed by a person'}
        </dd>
        <dt>Expected / received</dt>
        <dd>
          {formatMinorUnits(r.amount, r.currency)} / {formatMinorUnits(t.amount, t.currency)}
          {r.variable_symbol !== t.variable_symbol && (
            <span className="muted">
              {' '}
              · VS {r.variable_symbol} / {t.variable_symbol ?? 'none'}
            </span>
          )}
        </dd>
        <dt>Result</dt>
        <dd>{c.proposed ? <SummaryText summary={c.proposed} currency={r.currency} /> : '—'}</dd>
      </dl>
      <input
        placeholder="Reason, e.g. confirmed with the client: paid in two parts"
        value={reason}
        onChange={(e) => {
          key.reset()
          setReason(e.target.value)
        }}
        required
      />
      <ErrorText error={error} />
      <button type="submit" disabled={busy || reason.trim() === ''}>
        Allocate the whole transaction
      </button>
    </form>
  )
}

/** A request's allocations (active and revoked, with revocation) and its payment-change history. */
export function RequestPayments({
  path,
  prefix,
  request: r,
  readOnly,
  onChanged,
}: {
  path: string
  prefix: string
  request: PaymentRequest
  readOnly: boolean
  onChanged: (r: PaymentRequest) => void
}) {
  const [allocations, setAllocations] = useState<Allocation[]>([])
  const [changes, setChanges] = useState<PaymentChange[]>([])
  const [revoking, setRevoking] = useState<string | null>(null)
  const { busy, error, run } = useAction()

  const load = useCallback(async () => {
    setAllocations((await api<{ items: Allocation[] }>('GET', `${path}/allocations`)).items)
    setChanges((await api<{ items: PaymentChange[] }>('GET', `${path}/changes?limit=200`)).items)
  }, [path])

  useEffect(() => {
    void run(load)
  }, [run, load, r.version])

  const revoke = async (a: Allocation, reason: string, key: string) => {
    let done = false
    await run(async () => {
      const transaction = await api<IncomingTransaction>('GET', `${prefix}/transactions/${a.transaction_id}`)
      const outcome = await api<AllocationOutcome>(
        'POST',
        `${prefix}/allocations/${a.id}/revoke`,
        { expected_allocation_version: a.version, expected_transaction_version: transaction.version, expected_request_version: r.version, reason },
        { idempotencyKey: key },
      )
      setRevoking(null)
      onChanged(outcome.payment_request)
      done = true
    })
    return done
  }

  return (
    <>
      <h3>Received payments</h3>
      {r.open_review_case_ids && r.open_review_case_ids.length > 0 && (
        <p className="error">
          A transaction allocated to this request is under review ({r.open_review_case_ids.join(', ')}); it is not counted until a person
          resolves the review and allocates it again. See Review.
        </p>
      )}
      <ErrorText error={error} />
      {allocations.length === 0 ? (
        <p className="muted">No transaction has been allocated to this request.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Amount</th>
              <th>Transaction</th>
              <th>How</th>
              <th>State</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {allocations.map((a) => (
              <tr key={a.id}>
                <td>{formatMinorUnits(a.amount, a.currency)}</td>
                <td>
                  <code>{a.transaction_id}</code>
                </td>
                <td>
                  {words(a.method)} by {a.created_by}, {formatDate(a.created_at)}
                  {a.reason && <div className="muted">{a.reason}</div>}
                  {a.evidence !== 'live' && <span className="badge">{a.evidence}</span>}
                </td>
                <td>
                  <span className={`badge badge-${a.state === 'active' ? 'active' : 'cancelled'}`}>{a.state}</span>
                  {a.state === 'revoked' && (
                    <div className="muted">
                      {words(a.revocation_code)} by {a.revoked_by}, {formatDate(a.revoked_at)}
                      {a.revocation_reason && `: ${a.revocation_reason}`}
                    </div>
                  )}
                </td>
                <td>
                  {a.state === 'active' && !readOnly && (
                    <button className="secondary" onClick={() => setRevoking(revoking === a.id ? null : a.id)}>
                      {revoking === a.id ? 'Close' : 'Revoke…'}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {revoking && (
        <ReasonForm
          label="Revoke allocation"
          placeholder="Why, e.g. it belongs to another booking"
          danger
          busy={busy}
          onSubmit={(reason, key) => revoke(allocations.find((a) => a.id === revoking)!, reason, key)}
        />
      )}

      <h3>Payment history</h3>
      {changes.length === 0 ? (
        <p className="muted">No changes of the paid amount or status yet.</p>
      ) : (
        <ol>
          {changes.map((c) => (
            <li key={c.event_id}>
              {formatDate(c.created_at)}: {words(c.cause)} · {words(c.before.status)} → {words(c.after.status)}, paid{' '}
              {formatMinorUnits(c.before.paid_amount, c.currency)} → {formatMinorUnits(c.after.paid_amount, c.currency)}
              <span className="muted"> ({c.actor})</span>
            </li>
          ))}
        </ol>
      )}
    </>
  )
}
