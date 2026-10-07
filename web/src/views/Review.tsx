import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, type ListResponse, type ReviewCase, type ReviewCaseDetail, type ReviewDecision, type ReviewIssue, type Tenant } from '../api'
import { ErrorText, Section } from '../components'
import { formatDate, formatMinorUnits, useAction, useIdempotencyKey, words } from '../util'
import { HandlingBadge } from './Matching'
import { explain, ORIGIN_LABELS } from './matchingText'

const ISSUE_LABELS: Record<ReviewIssue, string> = {
  unverified_candidate: 'payment from an unverified email',
  possible_duplicate: 'possible duplicate',
  disputed: 'disputed transaction',
}

const DECISIONS: Record<ReviewDecision, { label: string; help: string }> = {
  confirm_payment: { label: 'Confirm the payment', help: 'It is a real booked receipt. It can then be allocated by hand, never automatically.' },
  confirm_distinct: { label: 'Separate payments', help: 'Each record is a real, separate payment; each can be allocated by hand.' },
  mark_duplicate: { label: 'Copies of one payment', help: 'Keep one record; the others are copies and are never counted.' },
  keep_existing: { label: 'Keep the recorded facts', help: 'The recorded transaction is right; the contradicting evidence is set aside.' },
  reject: { label: 'Not a valid receipt', help: 'Exclude it from payments. The evidence is kept.' },
}

/** Decisions that fit a case; the API checks them again. */
function decisionsFor(detail: ReviewCaseDetail): ReviewDecision[] {
  const count = detail.transactions.length
  switch (detail.case.issue) {
    case 'unverified_candidate':
      return ['confirm_payment', 'reject']
    case 'possible_duplicate':
      return count >= 2 ? ['confirm_distinct', 'mark_duplicate', 'reject'] : ['confirm_payment', 'reject']
    case 'disputed':
      return ['keep_existing', 'reject']
  }
}

/**
 * Review cases (Feature 9): payments the parser could not accept on its own. The original
 * evidence is shown as received; a decision records a person's confirmation next to it and never
 * turns it into passing authentication.
 */
export function Review({ prefix, owner, readOnly = false }: { prefix: string; owner: Tenant; readOnly?: boolean }) {
  const basePath = `${prefix}/review-cases`
  const [state, setState] = useState<'open' | 'resolved'>('open')
  const [items, setItems] = useState<ReviewCase[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [openId, setOpenId] = useState<string | null>(null)
  const { busy, error, run } = useAction()

  const loadPage = useCallback(
    async (after: string | null) => {
      const query = new URLSearchParams({ limit: '50', state })
      if (after) query.set('cursor', after)
      const page = await api<ListResponse<ReviewCase>>('GET', `${basePath}?${query}`)
      setItems((current) => (after ? [...current, ...page.items] : page.items))
      setCursor(page.next_cursor)
    },
    [basePath, state],
  )

  useEffect(() => {
    void run(() => loadPage(null))
  }, [run, loadPage])

  return (
    <>
      <Section title={`Review: ${owner.configuration.name}`}>
        <p className="muted">
          Payments that need a person: emails whose sender could not be verified, notifications that may describe the same payment,
          and transactions contradicted by later evidence. Until a case is resolved, its transactions are not counted as paid money.
        </p>
        <div className="row">
          <select value={state} onChange={(e) => setState(e.target.value as 'open' | 'resolved')}>
            <option value="open">open</option>
            <option value="resolved">resolved</option>
          </select>
          <button className="secondary" disabled={busy} onClick={() => void run(() => loadPage(null))}>
            Refresh
          </button>
        </div>
        <ErrorText error={error} />
        {items.length === 0 && !busy && <p className="muted">No {state} review cases.</p>}
        {items.length > 0 && (
          <table>
            <thead>
              <tr>
                <th>Opened</th>
                <th>Issue</th>
                <th>Reason</th>
                <th>State</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {items.map((c) => (
                <tr key={c.id}>
                  <td>{formatDate(c.created_at)}</td>
                  <td>{ISSUE_LABELS[c.issue]}</td>
                  <td>
                    {c.reason_message} <span className="muted">({c.reason_code})</span>
                  </td>
                  <td>
                    <span className={`badge badge-${c.state === 'open' ? 'suspended' : 'active'}`}>{c.state}</span>
                    {c.resolution && <div className="muted">{words(c.resolution)}</div>}
                  </td>
                  <td>
                    <button className="secondary" onClick={() => setOpenId(openId === c.id ? null : c.id)}>
                      {openId === c.id ? 'Close' : 'Open'}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {cursor && (
          <button className="secondary" disabled={busy} onClick={() => void run(() => loadPage(cursor))}>
            Load more
          </button>
        )}
      </Section>
      {openId && (
        <CaseDetail
          key={openId}
          path={`${basePath}/${openId}`}
          readOnly={readOnly}
          onChanged={(c) => setItems((current) => current.map((i) => (i.id === c.id ? c : i)))}
        />
      )}
    </>
  )
}

function CaseDetail({ path, readOnly, onChanged }: { path: string; readOnly: boolean; onChanged: (c: ReviewCase) => void }) {
  const [detail, setDetail] = useState<ReviewCaseDetail | null>(null)
  const [decision, setDecision] = useState<ReviewDecision | ''>('')
  const [canonical, setCanonical] = useState('')
  const [reason, setReason] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const key = useIdempotencyKey()
  const { busy, error, run, setError } = useAction()

  const load = useCallback(async () => setDetail(await api<ReviewCaseDetail>('GET', path)), [path])

  useEffect(() => {
    void run(load)
  }, [run, load])

  if (!detail) return <ErrorText error={error} />
  const c = detail.case
  const versions = Object.fromEntries(detail.transactions.map((t) => [t.id, t.version]))
  const allowed = decisionsFor(detail)

  const submit = (suffix: 'resolve' | 'reopen', body: Record<string, unknown>, message: string) =>
    run(async () => {
      try {
        const next = await api<ReviewCaseDetail>(
          'POST',
          `${path}/${suffix}`,
          { ...body, reason: reason.trim(), expected_version: c.version, expected_transaction_versions: versions },
          { idempotencyKey: key.current() },
        )
        key.reset()
        setDetail(next)
        onChanged(next.case)
        setReason('')
        setDecision('')
        setNotice(message)
      } catch (e) {
        if (e instanceof ApiError && e.status === 412) {
          key.reset()
          await load()
          setNotice('The case changed in the meantime; the current evidence is shown. Check it and decide again.')
        }
        throw e
      }
    })

  const edit = (change: () => void) => {
    key.reset()
    setError(null)
    setNotice(null)
    change()
  }

  return (
    <Section title={`Review case: ${ISSUE_LABELS[c.issue]}`}>
      <dl>
        <dt>State</dt>
        <dd>
          <span className={`badge badge-${c.state === 'open' ? 'suspended' : 'active'}`}>{c.state}</span>
          {c.resolution && ` · last decision: ${words(c.resolution)}`} <span className="muted">version {c.version}</span>
        </dd>
        <dt>Why</dt>
        <dd>
          {c.reason_message} <span className="muted">({c.reason_code})</span>
        </dd>
        {c.inbound_message_id && (
          <>
            <dt>Email</dt>
            <dd>
              <code>{c.inbound_message_id}</code> <span className="muted">Open it under Inbound emails.</span>
            </dd>
          </>
        )}
      </dl>

      <h3>Evidence as received</h3>
      <p className="muted">Recorded by the parser and the receiving mail system. A decision below never changes it.</p>
      {detail.parse_runs.map((r) => (
        <div key={r.id} className="forwarding">
          <strong>
            Parse run {r.generation}: {words(r.outcome)}
          </strong>{' '}
          <span className="muted">
            {r.bank_profile} {r.parser_version}, {formatDate(r.completed_at)}
          </span>
          <dl>
            <dt>Sender verification</dt>
            <dd>
              {r.trust_decision ?? '—'}
              {r.trust && (
                <span className="muted">
                  {' '}
                  · from {r.trust.evidence.from_address ?? 'unknown'} · SPF {r.trust.evidence.spf}, DKIM {r.trust.evidence.dkim}, DMARC{' '}
                  {r.trust.evidence.dmarc} ({r.trust.evidence.authentication_origin ?? 'no receiving-system result'})
                </span>
              )}
            </dd>
            <dt>Reasons</dt>
            <dd>{r.reasons.map((x) => `${x.message} (${x.code})`).join('; ') || '—'}</dd>
            {r.candidate && (
              <>
                <dt>Extracted payment</dt>
                <dd>
                  {r.candidate.amount !== null ? formatMinorUnits(r.candidate.amount, r.candidate.currency ?? '') : 'no amount'}, booked{' '}
                  {r.candidate.booked_on ?? '—'}, VS {r.candidate.variable_symbol ?? 'none'}, destination {words(r.candidate.destination)}
                  {r.candidate.sender_account && `, from ${r.candidate.sender_account}`}
                </dd>
              </>
            )}
          </dl>
        </div>
      ))}

      {detail.transactions.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>Transaction</th>
              <th>Amount</th>
              <th>Booked / VS</th>
              <th>Received</th>
              <th>Parser / decision</th>
            </tr>
          </thead>
          <tbody>
            {detail.transactions.map((t) => (
              <tr key={t.id}>
                <td>
                  <code>{t.id}</code>
                  <div className="muted">ref {t.bank_transaction_id ?? 'none'}</div>
                </td>
                <td>{formatMinorUnits(t.amount, t.currency)}</td>
                <td>
                  {t.booked_on} / {t.variable_symbol ?? '—'}
                </td>
                <td>
                  {ORIGIN_LABELS[t.origin_path]}
                  <div className="muted">{t.evidence} evidence</div>
                </td>
                <td>
                  {words(t.ingestion_status)} <HandlingBadge transaction={t} />
                  {t.matching?.manually_confirmed && <div>confirmed by a person</div>}
                  {t.matching?.manual_block && <div className="muted">{explain(t.matching.manual_block)}</div>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {notice && <p className="notice">{notice}</p>}
      <ErrorText error={error} />

      {!readOnly && c.state === 'open' && (
        <form
          onSubmit={(e) => {
            e.preventDefault()
            if (!decision) return
            void submit(
              'resolve',
              { decision, canonical_transaction_id: decision === 'mark_duplicate' ? canonical || null : null },
              'Decision saved. Allocate a confirmed receipt by hand under Bank transactions.',
            )
          }}
        >
          <h3>Decision</h3>
          {allowed.map((d) => (
            <label key={d} className="row">
              <input type="radio" name="decision" checked={decision === d} onChange={() => edit(() => setDecision(d))} />
              <span>
                <strong>{DECISIONS[d].label}</strong> <span className="muted">{DECISIONS[d].help}</span>
              </span>
            </label>
          ))}
          {decision === 'mark_duplicate' && (
            <label>
              The record to keep
              <select value={canonical} onChange={(e) => edit(() => setCanonical(e.target.value))} required>
                <option value="">choose…</option>
                {detail.transactions.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.id} ({ORIGIN_LABELS[t.origin_path]}, received {formatDate(t.received_at)})
                  </option>
                ))}
              </select>
            </label>
          )}
          <label>
            What did you check?
            <input
              placeholder="e.g. Checked the account statement in George: one payment of 450 CZK"
              value={reason}
              onChange={(e) => edit(() => setReason(e.target.value))}
              required
            />
          </label>
          <p className="muted">
            Recorded as a manual confirmation by you, separately from the email's authentication results. It does not trust the sender
            for future emails.
          </p>
          <button type="submit" disabled={busy || !decision || reason.trim() === ''}>
            Save decision
          </button>
        </form>
      )}

      {!readOnly && c.state === 'resolved' && (
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault()
            if (!confirm('Reopen the case? Its transactions stop counting as paid money, and any allocation of them is revoked.')) return
            void submit('reopen', {}, 'Reopened. The transactions need a new decision.')
          }}
        >
          <input placeholder="Why the decision must change" value={reason} onChange={(e) => edit(() => setReason(e.target.value))} required />
          <button type="submit" className="danger" disabled={busy || reason.trim() === ''}>
            Reopen
          </button>
        </form>
      )}

      <h3>Decisions</h3>
      {detail.decisions.length === 0 ? (
        <p className="muted">None yet.</p>
      ) : (
        <ol>
          {detail.decisions.map((d) => (
            <li key={d.id}>
              {formatDate(d.created_at)}: <strong>{words(d.action)}</strong> by {d.actor}: {d.reason}
              {d.canonical_transaction_id && <span className="muted"> · kept {d.canonical_transaction_id}</span>}
              {d.created_transaction_id && <span className="muted"> · created transaction {d.created_transaction_id}</span>}
            </li>
          ))}
        </ol>
      )}
    </Section>
  )
}
