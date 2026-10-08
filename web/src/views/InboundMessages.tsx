import { useCallback, useEffect, useState } from 'react'
import {
  api,
  type BankConnection,
  type InboundMessage,
  type InboundMessageSource,
  type ListResponse,
  type ParseHistory,
  type ParseOutcome,
  type ParseRun,
  type Tenant,
} from '../api'
import { ErrorText, Section } from '../components'
import { formatDate, formatMinorUnits, useAction } from '../util'

interface Filters {
  bank_connection_id: string
  content_state: '' | 'pending' | 'available' | 'failed' | 'expired' | 'erased'
  hold_reason: string
}

const NO_FILTERS: Filters = { bank_connection_id: '', content_state: '', hold_reason: '' }

const HOLD_LABELS: Record<string, string> = {
  address_retired: 'sent to a retired address',
  bank_connection_archived: 'bank connection archived',
  tenant_not_active: 'tenant not active',
  recipient_mismatch: 'recipients differ from the delivery; held for review',
  address_paused: 'address paused for foreign mail',
}

/**
 * Received bank-notification emails of one tenant: the selected tenant (`/v1/tenant`) or a direct
 * child (`/v1/subtenants/{id}`). Quarantined emails (unknown or ambiguous recipients) are not
 * shown here; only operators see them.
 */
export function InboundMessages({ prefix, owner, readOnly = false }: { prefix: string; owner: Tenant; readOnly?: boolean }) {
  const basePath = `${prefix}/inbound-messages`
  const [connections, setConnections] = useState<BankConnection[]>([])
  const [messages, setMessages] = useState<InboundMessage[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [filters, setFilters] = useState<Filters>(NO_FILTERS)
  const [openId, setOpenId] = useState<string | null>(null)
  const { busy, error, run } = useAction()

  const loadPage = useCallback(
    async (after: string | null) => {
      const query = new URLSearchParams({ limit: '50' })
      for (const [key, value] of Object.entries(filters)) if (value) query.set(key, value)
      if (after) query.set('cursor', after)
      const page = await api<ListResponse<InboundMessage>>('GET', `${basePath}?${query}`)
      setMessages((current) => (after ? [...current, ...page.items] : page.items))
      setCursor(page.next_cursor)
    },
    [basePath, filters],
  )

  useEffect(() => {
    void run(() => loadPage(null))
  }, [run, loadPage])

  useEffect(() => {
    void run(async () =>
      setConnections((await api<ListResponse<BankConnection>>('GET', `${prefix}/bank-connections?include_archived=true`)).items),
    )
  }, [run, prefix])

  const accountOf = (id: string) => connections.find((c) => c.id === id)
  const updateMessage = useCallback(
    (next: InboundMessage) => setMessages((current) => current.map((m) => (m.id === next.id ? { ...m, ...next } : m))),
    [],
  )

  return (
    <>
      <Section title={`Inbound emails: ${owner.configuration.name}`}>
        <p className="muted">
          Emails received at this tenant's notification addresses. Receiving an email changes no payment. Bank parsing records
          incoming transactions separately; matching them to reservations is a later feature. Email content is shown as text; HTML is never rendered.
        </p>
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault()
            void run(() => loadPage(null))
          }}
        >
          <select value={filters.bank_connection_id} onChange={(e) => setFilters({ ...filters, bank_connection_id: e.target.value })}>
            <option value="">any account</option>
            {connections.map((c) => (
              <option key={c.id} value={c.id}>
                {c.label ? `${c.label} (${c.account_number})` : c.account_number}
              </option>
            ))}
          </select>
          <select
            value={filters.content_state}
            onChange={(e) => setFilters({ ...filters, content_state: e.target.value as Filters['content_state'] })}
          >
            <option value="">any content state</option>
            <option value="pending">pending</option>
            <option value="available">available</option>
            <option value="failed">failed</option>
            <option value="expired">expired</option>
            <option value="erased">erased</option>
          </select>
          <select value={filters.hold_reason} onChange={(e) => setFilters({ ...filters, hold_reason: e.target.value })}>
            <option value="">held or not</option>
            <option value="none">not held</option>
            {Object.entries(HOLD_LABELS).map(([value, label]) => (
              <option key={value} value={value}>
                held: {label}
              </option>
            ))}
          </select>
          <button type="button" className="secondary" onClick={() => setFilters(NO_FILTERS)}>
            Clear filters
          </button>
          <button type="button" className="secondary" disabled={busy} onClick={() => void run(() => loadPage(null))}>
            Refresh
          </button>
        </form>
        <ErrorText error={error} />
        {messages.length === 0 && !busy && <p className="muted">No emails received.</p>}
        {messages.length > 0 && (
          <table>
            <thead>
              <tr>
                <th>Received</th>
                <th>From</th>
                <th>Subject</th>
                <th>Account</th>
                <th>Content</th>
                <th>Parsing</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {messages.map((m) => (
                <tr key={m.id}>
                  <td>{formatDate(m.received_at)}</td>
                  <td>{m.header_from ?? '—'}</td>
                  <td>{m.subject ?? '—'}</td>
                  <td>
                    <code>{accountOf(m.bank_connection_id)?.account_number ?? m.bank_connection_id}</code>
                  </td>
                  <td>
                    <ContentState message={m} />
                  </td>
                  <td>
                    <OutcomeBadge outcome={m.latest_parse_outcome} />
                  </td>
                  <td>
                    <button className="secondary" onClick={() => setOpenId(openId === m.id ? null : m.id)}>
                      {openId === m.id ? 'Close' : 'Open'}
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
        <MessageDetail
          key={openId}
          path={`${basePath}/${openId}`}
          readOnly={readOnly}
          onChanged={updateMessage}
        />
      )}
    </>
  )
}

function ContentState({ message: m }: { message: InboundMessage }) {
  return (
    <>
      <span
        className={`badge badge-${m.content_state === 'failed' ? 'suspended' : m.content_state === 'available' ? 'active' : m.content_state === 'erased' || m.content_state === 'expired' ? 'deleted' : 'pending'}`}
      >
        {m.content_state === 'erased' ? 'content erased' : m.content_state === 'expired' ? 'content expired' : m.content_state}
      </span>
      {m.hold_reason && <span className="badge badge-suspended">held</span>}
      {m.source_kind === 'fixture' && <span className="badge">fixture</span>}
      {m.environment === 'test' && <span className="badge">test</span>}
      {m.sender_class === 'tenant_forward' && <span className="badge">forwarded</span>}
      {m.sender_class === 'forwarding_confirmation' && <span className="badge">forwarding confirmation</span>}
    </>
  )
}

function MessageDetail({ path, readOnly, onChanged }: { path: string; readOnly: boolean; onChanged: (m: InboundMessage) => void }) {
  const [message, setMessage] = useState<InboundMessage | null>(null)
  const [source, setSource] = useState<InboundMessageSource | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const { busy, error, run } = useAction()

  const load = useCallback(async () => {
    const next = await api<InboundMessage>('GET', path)
    setMessage(next)
    onChanged(next)
    setSource(next.source_available ? await api<InboundMessageSource>('GET', `${path}/source`) : null)
  }, [path, onChanged])

  useEffect(() => {
    void run(load)
  }, [run, load])

  if (!message) return <ErrorText error={error} />

  return (
    <Section title={message.subject ?? '(no subject)'}>
      <dl>
        <dt>From (header, not verified)</dt>
        <dd>{message.header_from ?? '—'}</dd>
        <dt>Delivered to</dt>
        <dd>
          {message.envelope_recipients.join(', ') || '—'} <span className="muted">({message.recipient_evidence})</span>
        </dd>
        <dt>Received</dt>
        <dd>{formatDate(message.received_at)}</dd>
        <dt>Delivered by</dt>
        <dd>
          <code>{message.provider_instance_id}</code> via {message.transport_kind}
          {message.source_kind === 'fixture' ? ' — test fixture, not live reception' : ''}
          {message.environment === 'test' ? ' (test environment)' : ''}
        </dd>
        <dt>Sender authentication</dt>
        <dd>
          <code>
            spf={message.sender_authentication.spf} dkim={message.sender_authentication.dkim} dmarc={message.sender_authentication.dmarc}
          </code>{' '}
          <span className="muted">{message.sender_authentication.origin ?? 'no receiving-system evidence'}</span>
        </dd>
        <dt>Content</dt>
        <dd>
          <ContentState message={message} /> {message.content_mode}
          {message.content_stored_at && `, stored ${formatDate(message.content_stored_at)}`}
        </dd>
        {message.hold_reason && (
          <>
            <dt>Processing held</dt>
            <dd>{HOLD_LABELS[message.hold_reason] ?? message.hold_reason}</dd>
          </>
        )}
        {message.content_error && (
          <>
            <dt>Content error</dt>
            <dd className="error">
              {message.content_error.message} ({message.content_error.code}, {formatDate(message.content_error.at)})
            </dd>
          </>
        )}
        {message.content_sha256 && (
          <>
            <dt>SHA-256</dt>
            <dd>
              <code>{message.content_sha256}</code>
            </dd>
          </>
        )}
      </dl>

      {message.content_mode === 'deferred' && (
        <>
          <h3>Retrievals</h3>
          <table>
            <thead>
              <tr>
                <th>#</th>
                <th>State</th>
                <th>Attempts</th>
                <th>Next attempt</th>
                <th>Last error</th>
                <th>Requested by</th>
              </tr>
            </thead>
            <tbody>
              {(message.retrievals ?? []).map((r) => (
                <tr key={r.id}>
                  <td>{r.generation}</td>
                  <td>{r.state}</td>
                  <td>{r.attempt_count}</td>
                  <td>{formatDate(r.next_attempt_at)}</td>
                  <td>{r.last_error ? `${r.last_error.code}: ${r.last_error.message}` : '—'}</td>
                  <td>
                    <code>{r.requested_by}</code>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}

      <div className="row">
        {!readOnly && message.content_state === 'failed' && message.content_mode === 'deferred' && (
          <button
            disabled={busy}
            onClick={() =>
              void run(async () => {
                const queued = await api<{ job_id: string; state: string; created: boolean }>('POST', `${path}/retry-fetch`)
                setNotice(queued.created ? 'Retrieval queued. Refresh in a moment.' : `A retrieval is already ${queued.state}.`)
                await load()
              })
            }
          >
            Retry retrieval
          </button>
        )}
        <button className="secondary" disabled={busy} onClick={() => void run(load)}>
          Refresh
        </button>
      </div>
      {notice && <p className="muted">{notice}</p>}
      <ErrorText error={error} />

      {message.content_state === 'erased' && (
        <p className="muted">The content was erased with the tenant. Only routing facts remain; the financial records are kept.</p>
      )}
      {message.content_state === 'expired' && (
        <p className="muted">The content was purged by the payer-data retention limit. The financial records are kept.</p>
      )}
      {message.content_state === 'available' && <ParsingView path={path} readOnly={readOnly} onChanged={() => void run(load)} />}

      {source && <SourceView source={source} />}
      {!source && message.content_state === 'available' && message.hold_reason === 'recipient_mismatch' && (
        <p className="muted">The content names different recipients than the delivery and is withheld until an operator reviews it.</p>
      )}
    </Section>
  )
}

/** Everything as plain text: React escapes it, and nothing from the email is rendered or loaded. */
function SourceView({ source }: { source: InboundMessageSource }) {
  const [tab, setTab] = useState<'text' | 'html' | 'headers'>(source.text ? 'text' : 'html')
  return (
    <>
      <h3>Source</h3>
      <dl>
        <dt>Recipients in content</dt>
        <dd>{source.content_recipients?.join(', ') ?? '—'}</dd>
        <dt>Attachments</dt>
        <dd>
          {source.attachments && source.attachments.length > 0
            ? `${source.attachments.length} (metadata only, not downloaded)`
            : 'none'}
        </dd>
      </dl>
      <div className="tabs">
        <button className={tab === 'text' ? 'active' : ''} onClick={() => setTab('text')}>
          Text
        </button>
        <button className={tab === 'html' ? 'active' : ''} onClick={() => setTab('html')}>
          HTML source
        </button>
        <button className={tab === 'headers' ? 'active' : ''} onClick={() => setTab('headers')}>
          Headers (sender-supplied)
        </button>
      </div>
      <pre className="source">
        {tab === 'text' && (source.text ?? '(no text part)')}
        {tab === 'html' && (source.html ?? '(no HTML part)')}
        {tab === 'headers' &&
          JSON.stringify({ headers: source.headers, attachments: source.attachments, authentication_raw: source.authentication_raw }, null, 2)}
      </pre>
    </>
  )
}

const OUTCOME_LABELS: Record<ParseOutcome, string> = {
  parsed: 'bank transaction recorded',
  duplicate: 'already recorded (duplicate notice)',
  not_payment: 'not an incoming payment',
  unsupported: 'unsupported',
  review_required: 'needs review',
  failed: 'failed',
}

export function OutcomeBadge({ outcome }: { outcome: ParseOutcome | null }) {
  if (!outcome) return <span className="muted">not parsed</span>
  const tone =
    outcome === 'parsed' || outcome === 'duplicate' ? 'active' : outcome === 'review_required' || outcome === 'failed' ? 'suspended' : 'pending'
  return <span className={`badge badge-${tone}`}>{OUTCOME_LABELS[outcome]}</span>
}

/**
 * Parse history of a stored message: which bank profile and parser version read it, whether the
 * source was trusted, what was extracted, and why it was or was not recorded. Values extracted by
 * a run under review are candidates, not received money.
 */
function ParsingView({ path, readOnly, onChanged }: { path: string; readOnly: boolean; onChanged: () => void }) {
  const [history, setHistory] = useState<ParseHistory | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const { busy, error, run } = useAction()

  const load = useCallback(async () => setHistory(await api<ParseHistory>('GET', `${path}/parse-runs`)), [path])

  useEffect(() => {
    void run(load)
  }, [run, load])

  const latest = history?.runs.at(-1) ?? null
  const activeJob = history?.jobs.find((j) => j.state === 'queued' || j.state === 'running' || j.state === 'retry_wait')
  const lastError = history?.jobs.filter((j) => j.last_error).at(-1)?.last_error

  return (
    <>
      <h3>Bank parsing</h3>
      {history && history.runs.length === 0 && <p className="muted">{activeJob ? `Parsing is ${activeJob.state}.` : 'Not parsed yet.'}</p>}
      {latest && <RunDetail run={latest} />}
      {activeJob && history && history.runs.length > 0 && <p className="muted">Parsing #{activeJob.generation} is {activeJob.state}.</p>}
      {lastError && !activeJob && (
        <p className="muted">
          Last parse job did not complete: {lastError.message} ({lastError.code})
        </p>
      )}
      <div className="row">
        {!readOnly && (
          <button
            disabled={busy || !!activeJob}
            onClick={() =>
              void run(async () => {
                const queued = await api<{ job_id: string; generation: number; state: string; created: boolean }>('POST', `${path}/reprocess`)
                setNotice(queued.created ? `Parsing #${queued.generation} queued. Refresh in a moment.` : `Parsing is already ${queued.state}.`)
                await load()
                onChanged()
              })
            }
          >
            Parse again
          </button>
        )}
        <button className="secondary" disabled={busy} onClick={() => void run(load)}>
          Refresh parsing
        </button>
      </div>
      {notice && <p className="muted">{notice}</p>}
      <ErrorText error={error} />
      {history && history.runs.length > 1 && (
        <table>
          <thead>
            <tr>
              <th>#</th>
              <th>Completed</th>
              <th>Parser</th>
              <th>Trust</th>
              <th>Outcome</th>
              <th>Reason</th>
              <th>Requested by</th>
            </tr>
          </thead>
          <tbody>
            {history.runs.map((r) => (
              <tr key={r.id}>
                <td>{r.generation}</td>
                <td>{formatDate(r.completed_at)}</td>
                <td>
                  <code>{r.bank_profile ? `${r.bank_profile} ${r.parser_version}` : '—'}</code>
                </td>
                <td>{r.trust_decision ?? '—'}</td>
                <td>
                  <OutcomeBadge outcome={r.outcome} />
                </td>
                <td>{r.reason_message}</td>
                <td>
                  <code>{r.requested_by}</code>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}

function RunDetail({ run: r }: { run: ParseRun }) {
  const c = r.candidate
  const recorded = r.outcome === 'parsed' || r.outcome === 'duplicate'
  return (
    <dl>
      <dt>Result</dt>
      <dd>
        <OutcomeBadge outcome={r.outcome} /> {r.reason_message} <span className="muted">({r.reason_code})</span>
      </dd>
      <dt>Bank profile</dt>
      <dd>
        <code>{r.bank_profile ? `${r.bank_profile} ${r.parser_version}` : 'none for this bank'}</code>
        {r.template_id && (
          <span className="muted">
            {' '}
            template {r.template_id} from the {r.source_part} part
          </span>
        )}
      </dd>
      <dt>Source trust</dt>
      <dd>
        {r.trust_decision ?? '—'}
        {r.trust && (
          <span className="muted">
            {' '}
            (from {r.trust.evidence.from_address ?? 'no valid sender'}, dmarc={r.trust.evidence.dmarc},{' '}
            {r.trust.evidence.authentication_origin ?? 'no receiving-system evidence'})
          </span>
        )}
      </dd>
      {r.reasons.length > 0 && (
        <>
          <dt>Reasons</dt>
          <dd>
            <ul>
              {r.reasons.map((reason) => (
                <li key={reason.code + reason.message}>
                  {reason.message} <span className="muted">({reason.code})</span>
                </li>
              ))}
            </ul>
          </dd>
        </>
      )}
      {c && (
        <>
          <dt>{recorded ? 'Extracted' : 'Extracted candidate (not recorded)'}</dt>
          <dd>
            {c.amount !== null && c.currency ? formatMinorUnits(c.amount, c.currency) : `amount "${c.raw.amount ?? '—'}"`}
            {', VS '}
            {c.variable_symbol ?? (c.raw.variable_symbol ? `invalid "${c.raw.variable_symbol}"` : 'none')}
            {', booked '}
            {c.booked_on ?? c.raw.booked_on ?? '—'}
            {c.sender_name || c.sender_account || c.raw.sender_account
              ? `, from ${[c.sender_name, c.sender_account ?? c.raw.sender_account].filter(Boolean).join(' ')}`
              : ''}
            {c.bank_reference ? `, reference ${c.bank_reference}` : ''}
            <span className="muted"> (destination: {c.destination.replaceAll('_', ' ')})</span>
          </dd>
        </>
      )}
      {r.transaction_id && (
        <>
          <dt>Transaction</dt>
          <dd>
            <code>{r.transaction_id}</code>
          </dd>
        </>
      )}
    </dl>
  )
}
