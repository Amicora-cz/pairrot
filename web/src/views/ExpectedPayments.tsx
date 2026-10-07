import { useCallback, useEffect, useState } from 'react'
import {
  api,
  apiPng,
  ApiError,
  type BankConnection,
  type ListResponse,
  type PaymentInstructions,
  type PaymentRequest,
  type PaymentRequestStatus,
  type Tenant,
} from '../api'
import { ErrorText, Section } from '../components'
import { formatDate, formatMinorUnits, toMinorUnits, useAction, words } from '../util'
import { PaymentStatusBadge, RequestPayments } from './Matching'

interface Filters {
  external_id: string
  status: '' | PaymentRequestStatus
  overdue: '' | 'true' | 'false'
  bank_connection_id: string
}

const NO_FILTERS: Filters = { external_id: '', status: '', overdue: '', bank_connection_id: '' }

const STATUSES: PaymentRequestStatus[] = ['open', 'partially_paid', 'paid', 'overpaid', 'cancelled']

/**
 * Expected payments of one tenant: the selected tenant (`/v1/tenant`) or a direct child
 * (`/v1/subtenants/{id}`). `prefix` is that base path; the owner is always shown.
 */
export function ExpectedPayments({ prefix, owner, readOnly = false }: { prefix: string; owner: Tenant; readOnly?: boolean }) {
  const basePath = `${prefix}/payment-requests`
  const [connections, setConnections] = useState<BankConnection[]>([])
  const [requests, setRequests] = useState<PaymentRequest[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [filters, setFilters] = useState<Filters>(NO_FILTERS)
  const [openId, setOpenId] = useState<string | null>(null)
  const { busy, error, run } = useAction()

  const loadPage = useCallback(
    async (after: string | null) => {
      const query = new URLSearchParams({ limit: '50' })
      for (const [key, value] of Object.entries(filters)) if (value.trim()) query.set(key, value.trim())
      if (after) query.set('cursor', after)
      const page = await api<ListResponse<PaymentRequest>>('GET', `${basePath}?${query}`)
      setRequests((current) => (after ? [...current, ...page.items] : page.items))
      setCursor(page.next_cursor)
    },
    [basePath, filters],
  )

  useEffect(() => {
    void run(() => loadPage(null))
  }, [run, loadPage])

  useEffect(() => {
    // Archived connections are listed too: their requests stay in history.
    void run(async () =>
      setConnections((await api<ListResponse<BankConnection>>('GET', `${prefix}/bank-connections?include_archived=true`)).items),
    )
  }, [run, prefix])

  const replace = (next: PaymentRequest) =>
    setRequests((current) => (current.some((r) => r.id === next.id) ? current.map((r) => (r.id === next.id ? next : r)) : [next, ...current]))
  const opened = requests.find((r) => r.id === openId) ?? null

  return (
    <>
      <Section title={`Expected payments: ${owner.configuration.name}`}>
        <p className="muted">
          Owner: <strong>{owner.configuration.name}</strong> <code>{owner.external_id}</code>
          {owner.parent_tenant_id === null ? ' (root tenant)' : ' (subtenant)'}. A request is paid by allocated bank transactions:
          an exact payment is allocated automatically, partial payments and top-ups by hand under Bank transactions.
        </p>
        {!readOnly && (
          <CreateRequest
            basePath={basePath}
            connections={connections.filter((c) => c.status === 'active')}
            onCreated={(created) => {
              replace(created)
              setOpenId(created.id)
            }}
          />
        )}
      </Section>

      <Section title="Requests">
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault()
            void run(() => loadPage(null))
          }}
        >
          <input
            placeholder="external_id"
            value={filters.external_id}
            onChange={(e) => setFilters({ ...filters, external_id: e.target.value })}
          />
          <select value={filters.status} onChange={(e) => setFilters({ ...filters, status: e.target.value as Filters['status'] })}>
            <option value="">any state</option>
            {STATUSES.map((value) => (
              <option key={value} value={value}>
                {words(value)}
              </option>
            ))}
          </select>
          <select value={filters.overdue} onChange={(e) => setFilters({ ...filters, overdue: e.target.value as Filters['overdue'] })}>
            <option value="">overdue or not</option>
            <option value="true">overdue</option>
            <option value="false">not overdue</option>
          </select>
          <select value={filters.bank_connection_id} onChange={(e) => setFilters({ ...filters, bank_connection_id: e.target.value })}>
            <option value="">any account</option>
            {connections.map((c) => (
              <option key={c.id} value={c.id}>
                {connectionName(c)}
              </option>
            ))}
          </select>
          <button type="button" className="secondary" onClick={() => setFilters(NO_FILTERS)}>
            Clear filters
          </button>
        </form>
        <ErrorText error={error} />
        {requests.length === 0 && !busy && <p className="muted">No payment requests.</p>}
        {requests.length > 0 && (
          <table>
            <thead>
              <tr>
                <th>external_id</th>
                <th>Destination</th>
                <th>Expected</th>
                <th>Paid</th>
                <th>State</th>
                <th>Due</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {requests.map((r) => (
                <tr key={r.id}>
                  <td>
                    <code>{r.external_id}</code>
                  </td>
                  <td>
                    <code>{r.account_number}</code>
                  </td>
                  <td>{formatMinorUnits(r.amount, r.currency)}</td>
                  <td>{formatMinorUnits(r.paid_amount, r.currency)}</td>
                  <td>
                    <RequestState request={r} />
                  </td>
                  <td>{formatDate(r.due_at)}</td>
                  <td>
                    <button className="secondary" onClick={() => setOpenId(openId === r.id ? null : r.id)}>
                      {openId === r.id ? 'Close' : 'Open'}
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

      {opened && (
        <RequestDetail
          key={opened.id}
          basePath={basePath}
          prefix={prefix}
          owner={owner}
          request={opened}
          connection={connections.find((c) => c.id === opened.bank_connection_id) ?? null}
          readOnly={readOnly}
          onChanged={replace}
        />
      )}
    </>
  )
}

function connectionName(c: BankConnection) {
  return `${c.label ?? c.account_number}${c.label ? ` (${c.account_number})` : ''}${c.status === 'archived' ? ' – archived' : ''}`
}

function RequestState({ request: r }: { request: PaymentRequest }) {
  return (
    <>
      <PaymentStatusBadge status={r.status} />
      {r.is_overdue && <span className="badge badge-overdue">overdue</span>}
    </>
  )
}

/** `datetime-local` value in the browser's time zone, or '' for no due time. */
function toLocalInput(iso: string | null) {
  if (!iso) return ''
  const date = new Date(iso)
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 16)
}

/** A `datetime-local` value as an ISO timestamp with an offset (UTC), or null when empty. */
function fromLocalInput(value: string) {
  return value ? new Date(value).toISOString() : null
}

function CreateRequest({
  basePath,
  connections,
  onCreated,
}: {
  basePath: string
  connections: BankConnection[]
  onCreated: (request: PaymentRequest) => void
}) {
  const empty = {
    bank_connection_id: '',
    external_id: '',
    amount: '',
    custom_symbol: false,
    variable_symbol: '',
    message: '',
    description: '',
    due_at: '',
  }
  const [form, setForm] = useState(empty)
  const [notice, setNotice] = useState<string | null>(null)
  const { busy, error, run, setError } = useAction()
  const connectionId = form.bank_connection_id || connections[0]?.id || ''

  const edit = (change: Partial<typeof form>) => {
    setForm({ ...form, ...change })
    setError(null)
    setNotice(null)
  }
  const fieldError = (field: string) =>
    error instanceof ApiError && error.field === field ? <span className="field-error">{error.message}</span> : null
  const generalError = error instanceof ApiError && error.field ? null : error

  if (connections.length === 0) {
    return <p className="muted">Add an active bank account in Bank settings before creating payment requests.</p>
  }

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault()
        const amount = toMinorUnits(form.amount)
        if (amount === null) {
          setError(new ApiError(400, 'invalid_amount', 'Enter an amount in CZK with at most two decimal places, e.g. 450 or 450.50.', 'amount'))
          return
        }
        // The SPA only trims; validation happens in the API.
        const body = {
          bank_connection_id: connectionId,
          external_id: form.external_id.trim(),
          amount,
          currency: 'CZK',
          // Null asks Pairrot to assign the symbol. A retry after an error resends the same mode.
          variable_symbol: form.custom_symbol ? form.variable_symbol.trim() : null,
          message: form.message.trim() || null,
          description: form.description.trim() || null,
          due_at: fromLocalInput(form.due_at),
        }
        void run(async () => {
          const created = await api<PaymentRequest>('POST', basePath, body)
          setForm(empty)
          setNotice(
            created.replayed
              ? `A request with external_id ${created.external_id} already existed with the same details; it is shown unchanged.`
              : `Created ${created.external_id} with variable symbol ${created.variable_symbol}.`,
          )
          onCreated(created)
        })
      }}
    >
      <h3>New payment request</h3>
      <label>
        Bank account
        <select value={connectionId} onChange={(e) => edit({ bank_connection_id: e.target.value })}>
          {connections.map((c) => (
            <option key={c.id} value={c.id}>
              {connectionName(c)}
            </option>
          ))}
        </select>
        {fieldError('bank_connection_id')}
      </label>
      <label>
        External reference (external_id)
        <input placeholder="booking_123" value={form.external_id} onChange={(e) => edit({ external_id: e.target.value })} required />
        {fieldError('external_id')}
      </label>
      <label>
        Amount (CZK)
        <input inputMode="decimal" placeholder="450.00" value={form.amount} onChange={(e) => edit({ amount: e.target.value })} required />
        {fieldError('amount')}
      </label>
      <label>
        <input type="checkbox" checked={form.custom_symbol} onChange={(e) => edit({ custom_symbol: e.target.checked })} /> Enter my own
        variable symbol
        <span className="muted">Otherwise Pairrot assigns the next unused symbol for this bank account.</span>
      </label>
      {form.custom_symbol ? (
        <label>
          Variable symbol
          <input
            inputMode="numeric"
            maxLength={10}
            pattern="[0-9]{1,10}"
            placeholder="2026000123"
            value={form.variable_symbol}
            onChange={(e) => edit({ variable_symbol: e.target.value })}
            required
          />
          <span className="muted">Up to 10 digits. Leading zeros are ignored when checking for duplicates on the same account.</span>
          {fieldError('variable_symbol')}
        </label>
      ) : (
        fieldError('variable_symbol')
      )}
      <label>
        Message for the recipient (optional)
        <input placeholder="Yoga October" value={form.message} onChange={(e) => edit({ message: e.target.value })} />
        <span className="muted">Up to 60 characters; Czech letters are fine, * and % are not allowed. Cannot be changed later.</span>
        {fieldError('message')}
      </label>
      <label>
        Internal description (optional)
        <input value={form.description} onChange={(e) => edit({ description: e.target.value })} />
        {fieldError('description')}
      </label>
      <label>
        Due (optional, your local time)
        <input type="datetime-local" value={form.due_at} onChange={(e) => edit({ due_at: e.target.value })} />
        {fieldError('due_at')}
      </label>
      {fieldError('currency')}
      <ErrorText error={generalError} />
      {notice && <p className="notice">{notice}</p>}
      <button type="submit" disabled={busy}>
        {busy ? 'Creating…' : 'Create payment request'}
      </button>
    </form>
  )
}

interface Preview {
  data: PaymentInstructions
  imageUrl: string
}

function RequestDetail({
  basePath,
  prefix,
  owner,
  request: r,
  connection,
  readOnly,
  onChanged,
}: {
  basePath: string
  prefix: string
  owner: Tenant
  request: PaymentRequest
  connection: BankConnection | null
  readOnly: boolean
  onChanged: (request: PaymentRequest) => void
}) {
  const path = `${basePath}/${r.id}`
  const [description, setDescription] = useState(r.description ?? '')
  const [dueAt, setDueAt] = useState(toLocalInput(r.due_at))
  const [reason, setReason] = useState('')
  const [preview, setPreview] = useState<Preview | null>(null)
  const [stale, setStale] = useState<string | null>(null)
  const { busy, error, run, setError } = useAction()
  // Only an open request (nothing paid) hands out its original full-amount instructions or can be cancelled;
  // description and due time stay editable while something is left to pay.
  const open = r.status === 'open'
  const canChange = !readOnly && (open || r.status === 'partially_paid')
  const payable = open && connection?.status === 'active' && !readOnly

  // The list has no review links; the detail does.
  useEffect(() => {
    void run(async () => onChanged(await api<PaymentRequest>('GET', path)))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path])

  useEffect(() => () => {
    if (preview) URL.revokeObjectURL(preview.imageUrl)
  }, [preview])

  /** Applies a change; if someone else changed the request first, shows theirs instead of overwriting it. */
  const change = (action: () => Promise<PaymentRequest>) =>
    run(async () => {
      setStale(null)
      try {
        const next = await action()
        onChanged(next)
        setDescription(next.description ?? '')
        setDueAt(toLocalInput(next.due_at))
      } catch (e) {
        if (!(e instanceof ApiError && e.status === 412)) throw e
        const current = await api<PaymentRequest>('GET', path)
        onChanged(current)
        setDescription(current.description ?? '')
        setDueAt(toLocalInput(current.due_at))
        setStale('This request was changed elsewhere in the meantime. Your change was not saved; the current version is shown.')
      }
    })

  return (
    <Section title={`Payment request ${r.external_id}`}>
      <div className="connection">
        <div className="row">
          <RequestState request={r} />
          <span className="muted">version {r.version}</span>
        </div>
        <dl>
          <dt>Owner</dt>
          <dd>
            {owner.configuration.name} <code>{owner.external_id}</code>
          </dd>
          <dt>external_id</dt>
          <dd>
            <code>{r.external_id}</code>
          </dd>
          <dt>Destination</dt>
          <dd>
            <code>{r.account_number}</code> (<code>{r.iban}</code>)
            {connection?.status === 'archived' && <span className="muted"> – the bank connection is archived</span>}
          </dd>
          <dt>Expected</dt>
          <dd>{formatMinorUnits(r.amount, r.currency)}</dd>
          <dt>Paid</dt>
          <dd>{formatMinorUnits(r.paid_amount, r.currency)}</dd>
          <dt>Remaining</dt>
          <dd>{r.remaining_amount === null ? '— (cancelled)' : formatMinorUnits(r.remaining_amount, r.currency)}</dd>
          {r.overpaid_amount > 0 && (
            <>
              <dt>Overpaid</dt>
              <dd>{formatMinorUnits(r.overpaid_amount, r.currency)} (no refund is sent by Pairrot)</dd>
            </>
          )}
          <dt>Variable symbol</dt>
          <dd>{r.variable_symbol}</dd>
          <dt>Message</dt>
          <dd>{r.message ?? '—'}</dd>
          <dt>Description</dt>
          <dd>{r.description ?? '—'}</dd>
          <dt>Due</dt>
          <dd>
            {formatDate(r.due_at)}
            {r.is_overdue && ' (overdue)'}
          </dd>
          <dt>Created</dt>
          <dd>{formatDate(r.created_at)}</dd>
          <dt>Updated</dt>
          <dd>{formatDate(r.updated_at)}</dd>
          {r.status === 'cancelled' && (
            <>
              <dt>Cancelled</dt>
              <dd>
                {formatDate(r.cancelled_at)}
                {r.cancellation_reason && `: ${r.cancellation_reason}`}
              </dd>
            </>
          )}
        </dl>
      </div>

      {stale && <p className="error">{stale}</p>}
      <ErrorText error={error} />

      {payable && (
        <div className="payment-qr">
          <h3>Payment instructions</h3>
          <p className="muted">Saved when the request was created. A code for a real account may prefill a real payment in a banking app.</p>
          {!preview ? (
            <button
              className="secondary"
              disabled={busy}
              onClick={() =>
                void run(async () => {
                  const data = await api<PaymentInstructions>('GET', `${path}/payment-instructions`)
                  const image = await apiPng(`${path}/qr.png`)
                  setPreview({ data, imageUrl: URL.createObjectURL(image) })
                })
              }
            >
              Show QR code
            </button>
          ) : (
            <div className="qr-preview">
              <img src={preview.imageUrl} alt="Payment QR code" />
              <p>
                <code className="payload">{preview.data.payload}</code>
              </p>
              <p className="muted">
                {preview.data.format}, generator {preview.data.generator_version}
              </p>
              <div className="row">
                <button className="secondary" onClick={() => void navigator.clipboard.writeText(preview.data.payload)}>
                  Copy payment data
                </button>
                <a className="button" href={preview.imageUrl} download={`payment-qr-${preview.data.variable_symbol}.png`}>
                  Download PNG
                </a>
              </div>
            </div>
          )}
        </div>
      )}

      {canChange && (
        <>
          <form
            onSubmit={(e) => {
              e.preventDefault()
              setError(null)
              void change(() =>
                api<PaymentRequest>(
                  'PATCH',
                  path,
                  { description: description.trim() || null, due_at: fromLocalInput(dueAt) },
                  { ifMatch: r.version },
                ),
              )
            }}
          >
            <h3>Edit</h3>
            <label>
              Internal description
              <input value={description} onChange={(e) => setDescription(e.target.value)} />
            </label>
            <label>
              Due (your local time)
              <input type="datetime-local" value={dueAt} onChange={(e) => setDueAt(e.target.value)} />
            </label>
            <button type="submit" disabled={busy}>
              Save
            </button>
          </form>
          {open && (
          <form
            className="row"
            onSubmit={(e) => {
              e.preventDefault()
              if (!confirm(`Cancel ${r.external_id}? The record and its variable symbol are kept; it cannot be reopened.`)) return
              void change(() => api<PaymentRequest>('POST', `${path}/cancel`, { reason: reason.trim() || null }, { ifMatch: r.version }))
            }}
          >
            <input placeholder="Cancellation reason (optional)" value={reason} onChange={(e) => setReason(e.target.value)} />
            <button type="submit" className="danger" disabled={busy}>
              Cancel request
            </button>
          </form>
          )}
        </>
      )}

      {!open && r.status !== 'cancelled' && (
        <p className="muted">The request has received a payment, so its original full-amount QR code is not issued again.</p>
      )}
      <RequestPayments path={path} prefix={prefix} request={r} readOnly={readOnly} onChanged={onChanged} />
    </Section>
  )
}
