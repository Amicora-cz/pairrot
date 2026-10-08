import { useCallback, useEffect, useState } from 'react'
import { api, type AttentionItem, type AttentionKind, type AttentionSummary, type ListResponse, type Tenant } from '../api'
import { ErrorText, Section } from '../components'
import { formatDate, formatMinorUnits, useAction, words } from '../util'

const KINDS: { kind: AttentionKind; label: string; optIn?: boolean }[] = [
  { kind: 'unmatched_transaction', label: 'Unmatched receipts' },
  { kind: 'review_case', label: 'Review cases' },
  { kind: 'paused_address', label: 'Paused addresses' },
  { kind: 'failed_delivery', label: 'Failed webhook deliveries' },
  { kind: 'overpaid_request', label: 'Overpaid requests' },
  { kind: 'overdue_request', label: 'Overdue requests', optIn: true },
]

/** One line about an item, from its summary only (no sender data or free text is ever in it). */
function describe(item: AttentionItem) {
  const s = item.summary
  const money = (amount: unknown) => (typeof amount === 'number' ? formatMinorUnits(amount, String(s.currency ?? 'CZK')) : '—')
  switch (item.kind) {
    case 'unmatched_transaction':
      return `${money(s.amount)}, VS ${s.variable_symbol ?? '—'}: ${words(String(s.result ?? 'not matched yet'))}${
        s.external_id ? ` (suggested: ${s.external_id}, expected ${money(s.expected_amount)})` : ''
      }`
    case 'review_case':
      return `${words(String(s.issue))}, ${s.transaction_count} transaction(s): ${words(String(s.reason_code))}`
    case 'paused_address':
      return `${s.inbound_address} stopped processing because of foreign mail${s.operator_only_resume ? '; only support can resume it' : ''}`
    case 'failed_delivery':
      return `${s.event_type} ${s.state}${s.hold_reason ? ` (${words(String(s.hold_reason))})` : ''}${
        s.last_error_code ? `: ${words(String(s.last_error_code))}` : ''
      }`
    case 'overpaid_request':
      return `${s.external_id}: paid ${money(s.paid_amount)} of ${money(s.amount)}, ${money(s.excess_amount)} too much`
    case 'overdue_request':
      return `${s.external_id}: ${money(s.remaining_amount)} left, due ${formatDate(String(s.due_at))}`
  }
}

/**
 * Feature 16: what waits for a person, oldest first. A parent administrator also sees its direct
 * subtenants' items; items disappear when their cause is resolved, there is nothing to dismiss.
 */
export function Attention({ owner, canManageChildren }: { owner: Tenant; canManageChildren: boolean }) {
  const [acrossChildren, setAcrossChildren] = useState(canManageChildren)
  const [kinds, setKinds] = useState<AttentionKind[]>(KINDS.filter((k) => !k.optIn).map((k) => k.kind))
  const [items, setItems] = useState<AttentionItem[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [summary, setSummary] = useState<AttentionSummary | null>(null)
  const [names, setNames] = useState<Record<string, string>>({})
  const { busy, error, run } = useAction()
  const base = acrossChildren ? '/v1/management/attention' : '/v1/tenant/attention'

  const loadPage = useCallback(
    async (after: string | null) => {
      const query = new URLSearchParams({ limit: '50', kinds: kinds.join(',') })
      if (after) query.set('cursor', after)
      const page = await api<ListResponse<AttentionItem>>('GET', `${base}?${query}`)
      setItems((current) => (after ? [...current, ...page.items] : page.items))
      setCursor(page.next_cursor)
      if (!after) setSummary(await api<AttentionSummary>('GET', `${base}/summary?${new URLSearchParams({ kinds: kinds.join(',') })}`))
    },
    [base, kinds],
  )

  useEffect(() => {
    if (kinds.length > 0) void run(() => loadPage(null))
  }, [run, loadPage, kinds.length])

  useEffect(() => {
    if (!acrossChildren) return
    void run(async () => {
      const children = await api<ListResponse<Tenant>>('GET', '/v1/subtenants?limit=200')
      setNames(Object.fromEntries(children.items.map((c) => [c.id, c.configuration.name])))
    })
  }, [run, acrossChildren])

  const tenantName = (id: string) => (id === owner.id ? 'this tenant' : (names[id] ?? id))

  return (
    <Section title="Needs attention">
      <p className="muted">
        Receipts nobody matched, open review cases, paused addresses, failed webhook deliveries, and overpaid requests, oldest
        first. An item disappears when its cause is resolved.
      </p>
      <div className="row">
        {canManageChildren && (
          <label className="inline">
            <input type="checkbox" checked={acrossChildren} onChange={(e) => setAcrossChildren(e.target.checked)} /> include direct
            subtenants
          </label>
        )}
        {KINDS.map(({ kind, label }) => (
          <label key={kind} className="inline">
            <input
              type="checkbox"
              checked={kinds.includes(kind)}
              onChange={(e) => setKinds(e.target.checked ? [...kinds, kind] : kinds.filter((k) => k !== kind))}
            />{' '}
            {label}
            {summary?.by_kind[kind] ? <span className="badge badge-pending">{summary.by_kind[kind]}</span> : null}
          </label>
        ))}
      </div>
      <ErrorText error={error} />
      {kinds.length === 0 ? (
        <p className="muted">Select at least one kind.</p>
      ) : (
        <>
          <table>
            <thead>
              <tr>
                <th>Since</th>
                <th>Kind</th>
                <th>Tenant</th>
                <th>What</th>
                <th>Subject</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => (
                <tr key={`${item.kind}-${item.subject_id}`}>
                  <td>{formatDate(item.since)}</td>
                  <td>{words(item.kind)}</td>
                  <td>{tenantName(item.tenant_id)}</td>
                  <td>{describe(item)}</td>
                  <td>
                    <code>{item.subject_id}</code>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {items.length === 0 && <p className="muted">Nothing waits for a person.</p>}
          {cursor && (
            <button className="secondary" disabled={busy} onClick={() => void run(() => loadPage(cursor))}>
              Load more
            </button>
          )}
          {summary && acrossChildren && summary.by_tenant.length > 1 && (
            <p className="muted">
              {summary.by_tenant.map((t) => `${tenantName(t.tenant_id)}: ${t.total}`).join(' · ')}
            </p>
          )}
        </>
      )}
    </Section>
  )
}
