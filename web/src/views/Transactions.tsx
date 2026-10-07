import { useCallback, useEffect, useState } from 'react'
import { api, type BankConnection, type IncomingTransaction, type IngestionStatus, type ListResponse, type Tenant } from '../api'
import { ErrorText, Section } from '../components'
import { formatDate, formatMinorUnits, useAction } from '../util'

interface Filters {
  bank_connection_id: string
  ingestion_status: '' | IngestionStatus
  variable_symbol: string
  booked_from: string
  booked_to: string
}

const NO_FILTERS: Filters = { bank_connection_id: '', ingestion_status: '', variable_symbol: '', booked_from: '', booked_to: '' }

const STATUS_LABELS: Record<IngestionStatus, string> = {
  accepted: 'recorded',
  review_required: 'needs review',
  disputed: 'disputed',
}

const ORIGIN_LABELS: Record<IncomingTransaction['origin_path'], string> = {
  direct: 'directly from the bank',
  automatic_forward: 'automatically forwarded from a registered mailbox',
  manual_forward: 'forwarded by hand from a registered mailbox',
}

const EVIDENCE_LABELS: Record<IncomingTransaction['evidence'], string> = {
  live: 'live bank notification',
  test: 'test environment',
  fixture: 'test fixture',
}

/**
 * Incoming bank transactions recorded from notifications. "Bank transaction recorded" is all it
 * means: no reservation is marked paid here, and records under review or dispute are not money.
 */
export function Transactions({ prefix, owner }: { prefix: string; owner: Tenant }) {
  const basePath = `${prefix}/transactions`
  const [connections, setConnections] = useState<BankConnection[]>([])
  const [items, setItems] = useState<IncomingTransaction[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [filters, setFilters] = useState<Filters>(NO_FILTERS)
  const [open, setOpen] = useState<IncomingTransaction | null>(null)
  const { busy, error, run } = useAction()

  const loadPage = useCallback(
    async (after: string | null) => {
      const query = new URLSearchParams({ limit: '50' })
      for (const [key, value] of Object.entries(filters)) if (value) query.set(key, value)
      if (after) query.set('cursor', after)
      const page = await api<ListResponse<IncomingTransaction>>('GET', `${basePath}?${query}`)
      setItems((current) => (after ? [...current, ...page.items] : page.items))
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

  return (
    <>
      <Section title={`Bank transactions: ${owner.configuration.name}`}>
        <p className="muted">
          Incoming transfers recognized in bank notifications. A recorded transaction is not yet matched to any reservation;
          records that need review or are disputed are not counted as received money.
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
            value={filters.ingestion_status}
            onChange={(e) => setFilters({ ...filters, ingestion_status: e.target.value as Filters['ingestion_status'] })}
          >
            <option value="">any status</option>
            {Object.entries(STATUS_LABELS).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
          <input
            placeholder="Variable symbol"
            inputMode="numeric"
            value={filters.variable_symbol}
            onChange={(e) => setFilters({ ...filters, variable_symbol: e.target.value.trim() })}
          />
          <label>
            Booked from <input type="date" value={filters.booked_from} onChange={(e) => setFilters({ ...filters, booked_from: e.target.value })} />
          </label>
          <label>
            to <input type="date" value={filters.booked_to} onChange={(e) => setFilters({ ...filters, booked_to: e.target.value })} />
          </label>
          <button type="submit" disabled={busy}>
            Search
          </button>
          <button type="button" className="secondary" onClick={() => setFilters(NO_FILTERS)}>
            Clear filters
          </button>
        </form>
        <ErrorText error={error} />
        {items.length === 0 && !busy && <p className="muted">No bank transactions recorded.</p>}
        {items.length > 0 && (
          <table>
            <thead>
              <tr>
                <th>Booked</th>
                <th>Amount</th>
                <th>VS</th>
                <th>From</th>
                <th>Account</th>
                <th>Status</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {items.map((t) => (
                <tr key={t.id}>
                  <td>{t.booked_on}</td>
                  <td>{formatMinorUnits(t.amount, t.currency)}</td>
                  <td>{t.variable_symbol ?? '—'}</td>
                  <td>{[t.sender_name, t.sender_account].filter(Boolean).join(' ') || '—'}</td>
                  <td>
                    <code>{accountOf(t.bank_connection_id)?.account_number ?? t.bank_connection_id}</code>
                  </td>
                  <td>
                    <StatusBadges transaction={t} />
                  </td>
                  <td>
                    <button
                      className="secondary"
                      onClick={() =>
                        open?.id === t.id
                          ? setOpen(null)
                          : void run(async () => setOpen(await api<IncomingTransaction>('GET', `${basePath}/${t.id}`)))
                      }
                    >
                      {open?.id === t.id ? 'Close' : 'Open'}
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
      {open && <TransactionDetail transaction={open} />}
    </>
  )
}

function StatusBadges({ transaction: t }: { transaction: IncomingTransaction }) {
  return (
    <>
      <span className={`badge badge-${t.ingestion_status === 'accepted' ? 'active' : 'suspended'}`}>{STATUS_LABELS[t.ingestion_status]}</span>
      {t.evidence !== 'live' && <span className="badge">{EVIDENCE_LABELS[t.evidence]}</span>}
    </>
  )
}

function TransactionDetail({ transaction: t }: { transaction: IncomingTransaction }) {
  return (
    <Section title={`Bank transaction recorded: ${formatMinorUnits(t.amount, t.currency)}`}>
      <dl>
        <dt>Status</dt>
        <dd>
          <StatusBadges transaction={t} />
        </dd>
        <dt>Booked</dt>
        <dd>
          {t.booked_on}
          {t.occurred_at && <span className="muted"> (at {formatDate(t.occurred_at)})</span>}
        </dd>
        <dt>Notification received</dt>
        <dd>{formatDate(t.received_at)}</dd>
        <dt>Variable symbol</dt>
        <dd>{t.variable_symbol ?? 'none'}</dd>
        <dt>Sender</dt>
        <dd>{[t.sender_name, t.sender_account].filter(Boolean).join(' ') || '—'}</dd>
        <dt>Message for recipient</dt>
        <dd>{t.message ?? '—'}</dd>
        <dt>Bank reference</dt>
        <dd>{t.bank_transaction_id ?? 'none provided'}</dd>
        <dt>Recorded by</dt>
        <dd>
          <code>
            {t.bank_profile} {t.parser_version}
          </code>
          , {formatDate(t.created_at)}
        </dd>
        <dt>Evidence</dt>
        <dd>{EVIDENCE_LABELS[t.evidence]}</dd>
        <dt>Received</dt>
        <dd>{ORIGIN_LABELS[t.origin_path]}</dd>
        {t.ingestion_reasons.length > 0 && (
          <>
            <dt>Review history</dt>
            <dd>
              <ul>
                {t.ingestion_reasons.map((r) => (
                  <li key={`${r.at}-${r.code}`}>
                    {formatDate(r.at)}: {r.message} <span className="muted">({r.code})</span>
                  </li>
                ))}
              </ul>
            </dd>
          </>
        )}
        <dt>Supporting emails</dt>
        <dd>
          <ul>
            {(t.sources ?? []).map((s) => (
              <li key={s.inbound_message_id}>
                <code>{s.inbound_message_id}</code> <span className="muted">linked {formatDate(s.linked_at)}</span>
              </li>
            ))}
          </ul>
          <span className="muted">Open them under Inbound emails.</span>
        </dd>
      </dl>
    </Section>
  )
}
