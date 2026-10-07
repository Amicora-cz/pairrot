import { useCallback, useEffect, useState } from 'react'
import { api, type IssuedApiKey, type ListResponse, type Tenant, type TenantStatus } from '../api'
import { ErrorText, SecretOnce, Section, StatusBadge } from '../components'
import { formatDate, useAction } from '../util'
import { KeyManager } from './ApiKeys'
import { BankSettings } from './BankSettings'
import { ExpectedPayments } from './ExpectedPayments'
import { History } from './History'
import { InboundMessages } from './InboundMessages'
import { Review } from './Review'
import { Transactions } from './Transactions'

/** Direct children of the selected root tenant. Managing a child does not make you its member. */
export function Subtenants() {
  const [children, setChildren] = useState<Tenant[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [statusFilter, setStatusFilter] = useState<'' | TenantStatus>('')
  const [openId, setOpenId] = useState<string | null>(null)
  const [created, setCreated] = useState<IssuedApiKey | null>(null)
  const [form, setForm] = useState({ external_id: '', name: '', contact_email: '' })
  const { busy, error, run } = useAction()

  const loadPage = useCallback(
    async (after: string | null) => {
      const query = new URLSearchParams({ limit: '50' })
      if (statusFilter) query.set('status', statusFilter)
      if (after) query.set('cursor', after)
      const page = await api<ListResponse<Tenant>>('GET', `/v1/subtenants?${query}`)
      setChildren((current) => (after ? [...current, ...page.items] : page.items))
      setCursor(page.next_cursor)
    },
    [statusFilter],
  )

  useEffect(() => {
    void run(() => loadPage(null))
  }, [run, loadPage])

  const replace = (tenant: Tenant) => setChildren((current) => current.map((c) => (c.id === tenant.id ? tenant : c)))

  return (
    <>
      <Section title="Create subtenant">
        {created && <SecretOnce issued={created} onDismiss={() => setCreated(null)} />}
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault()
            void run(async () => {
              const result = await api<{ tenant: Tenant; api_key: IssuedApiKey }>('POST', '/v1/subtenants', {
                external_id: form.external_id,
                name: form.name,
                contact_email: form.contact_email.trim() === '' ? null : form.contact_email,
              })
              setCreated(result.api_key)
              setForm({ external_id: '', name: '', contact_email: '' })
              await loadPage(null)
            })
          }}
        >
          <input placeholder="external_id" value={form.external_id} onChange={(e) => setForm({ ...form, external_id: e.target.value })} required />
          <input placeholder="Name" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          <input
            type="email"
            placeholder="Contact email (optional)"
            value={form.contact_email}
            onChange={(e) => setForm({ ...form, contact_email: e.target.value })}
          />
          <button type="submit" disabled={busy}>
            Create
          </button>
        </form>
      </Section>

      <Section title="Subtenants">
        <label className="inline">
          Status{' '}
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value as '' | TenantStatus)}>
            <option value="">active and suspended</option>
            <option value="active">active</option>
            <option value="suspended">suspended</option>
            <option value="deleted">deleted</option>
          </select>
        </label>
        <ErrorText error={error} />
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>external_id</th>
              <th>Status</th>
              <th>Contact email</th>
              <th>Created</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {children.map((child) => (
              <tr key={child.id}>
                <td>{child.configuration.name}</td>
                <td>
                  <code>{child.external_id}</code>
                </td>
                <td>
                  <StatusBadge status={child.status} />
                </td>
                <td>{child.configuration.contact_email ?? '—'}</td>
                <td>{formatDate(child.created_at)}</td>
                <td>
                  <button className="secondary" onClick={() => setOpenId(openId === child.id ? null : child.id)}>
                    {openId === child.id ? 'Close' : 'Manage'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {cursor && (
          <button className="secondary" disabled={busy} onClick={() => void run(() => loadPage(cursor))}>
            Load more
          </button>
        )}
      </Section>

      {openId && children.some((c) => c.id === openId) && (
        <ChildDetail
          key={openId}
          child={children.find((c) => c.id === openId)!}
          onChanged={replace}
          onDeleted={() => {
            setOpenId(null)
            void run(() => loadPage(null))
          }}
        />
      )}
    </>
  )
}

function ChildDetail({ child, onChanged, onDeleted }: { child: Tenant; onChanged: (tenant: Tenant) => void; onDeleted: () => void }) {
  const [name, setName] = useState(child.configuration.name)
  const [contactEmail, setContactEmail] = useState(child.configuration.contact_email ?? '')
  const { busy, error, run } = useAction()
  const deleted = child.status === 'deleted'

  const patch = (body: Record<string, unknown>) =>
    run(async () => onChanged(await api<Tenant>('PATCH', `/v1/subtenants/${child.id}`, body)))

  return (
    <>
      <Section title={`Subtenant: ${child.configuration.name}`}>
        <form
          onSubmit={(e) => {
            e.preventDefault()
            void patch({ name, contact_email: contactEmail.trim() === '' ? null : contactEmail })
          }}
        >
          <label>
            Name
            <input value={name} onChange={(e) => setName(e.target.value)} disabled={deleted} required />
          </label>
          <label>
            Contact email
            <input type="email" value={contactEmail} onChange={(e) => setContactEmail(e.target.value)} disabled={deleted} />
          </label>
          <div className="row">
            <button type="submit" disabled={busy || deleted}>
              Save
            </button>
            {child.status === 'active' && (
              <button type="button" className="secondary" disabled={busy} onClick={() => void patch({ status: 'suspended' })}>
                Suspend
              </button>
            )}
            {child.status === 'suspended' && (
              <button type="button" className="secondary" disabled={busy} onClick={() => void patch({ status: 'active' })}>
                Reactivate
              </button>
            )}
            {!deleted && (
              <button
                type="button"
                className="danger"
                disabled={busy}
                onClick={() => {
                  if (!confirm(`Delete ${child.configuration.name}? All its keys are revoked and it cannot be restored.`)) return
                  void run(async () => {
                    await api('DELETE', `/v1/subtenants/${child.id}`)
                    onDeleted()
                  })
                }}
              >
                Delete
              </button>
            )}
          </div>
        </form>
        <ErrorText error={error} />
      </Section>
      {!deleted && (
        <Section title="Subtenant API keys">
          <KeyManager basePath={`/v1/subtenants/${child.id}/api-keys`} purposes={['tenant']} />
        </Section>
      )}
      {/* A suspended child can be inspected but not changed; a deleted child's connections are history. */}
      <BankSettings key={child.id} basePath={`/v1/subtenants/${child.id}/bank-connections`} owner={child} readOnly={child.status !== 'active'} />
      <ExpectedPayments key={`payments-${child.id}`} prefix={`/v1/subtenants/${child.id}`} owner={child} readOnly={child.status !== 'active'} />
      <InboundMessages key={`inbound-${child.id}`} prefix={`/v1/subtenants/${child.id}`} owner={child} readOnly={child.status !== 'active'} />
      <Transactions key={`transactions-${child.id}`} prefix={`/v1/subtenants/${child.id}`} owner={child} readOnly={child.status !== 'active'} />
      <Review key={`review-${child.id}`} prefix={`/v1/subtenants/${child.id}`} owner={child} readOnly={child.status !== 'active'} />
      <History path={`/v1/subtenants/${child.id}/audit-events`} title="Subtenant history" />
    </>
  )
}
