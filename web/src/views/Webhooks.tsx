import { useCallback, useEffect, useState } from 'react'
import {
  api,
  ApiError,
  type ListResponse,
  type SignedWebhookEndpoint,
  type Tenant,
  type WebhookDelivery,
  type WebhookDeliveryDetail,
  type WebhookDeliveryState,
  type WebhookEndpoint,
  type WebhookEvent,
  type WebhookEventType,
  type WebhookHealth,
  type WebhookScope,
  WEBHOOK_EVENT_TYPES,
} from '../api'
import { ErrorText, Section } from '../components'
import { formatDate, useAction, useIdempotencyKey, words } from '../util'

const HEALTH: Record<WebhookHealth, { label: string; tone: string; help: string }> = {
  paused: { label: 'paused', tone: 'pending', help: 'Payment changes are recorded and wait here; nothing is sent until you activate the endpoint.' },
  configured: {
    label: 'configured, not verified',
    tone: 'pending',
    help: 'A URL is saved, but no request has been accepted yet. Send a test to check the receiver.',
  },
  test_accepted: { label: 'test accepted', tone: 'active', help: 'The receiver accepted a test. No real payment change has been delivered yet.' },
  delivering: { label: 'delivering', tone: 'active', help: 'The receiver accepts real payment changes.' },
  failing: { label: 'failing', tone: 'suspended', help: 'Some deliveries are waiting for a retry, failed, or are held. See the list below.' },
}

const HOLDS: Record<string, string> = {
  destination_changed: 'The URL changed after this was queued. It is not sent to the new host automatically: replay the event to the current destination.',
  destination_not_allowed: 'The destination is not allowed (for example it resolves to a private address). Fix the URL, then replay.',
  environment_mismatch: 'The event and the endpoint are in different environments (live/test); it is never sent.',
  tenant_not_active: 'The tenant or its parent is not active. Sending resumes automatically after reactivation.',
  not_authorized: 'The endpoint is no longer allowed to receive this tenant’s changes (scope or capability changed).',
  signing_key_unavailable: 'No signing key is available. Rotate the secret; sending resumes automatically.',
}

const STATES: WebhookDeliveryState[] = ['pending', 'running', 'retry_wait', 'delivered', 'failed', 'held', 'abandoned']

const EVENT_TYPE_HELP: Record<WebhookEventType, string> = {
  'payment_request.updated': 'a payment request’s paid amount or status changed',
  'transaction.recorded': 'a bank receipt was recorded and matched (or not)',
  'transaction.updated': 'a receipt was allocated, revoked, ignored, restored, or needs review',
  'review_case.opened': 'a receipt needs a person’s decision',
  'review_case.updated': 'a review case got evidence, was resolved, or reopened',
  'inbound_address.paused': 'the notification address stopped processing because of foreign mail',
  'inbound_address.resumed': 'the notification address processes mail again',
}

/** Feature 16: which event types the endpoint receives. Payment changes only by default. */
function EventTypePicker({ value, onChange, disabled }: { value: WebhookEventType[]; onChange: (next: WebhookEventType[]) => void; disabled?: boolean }) {
  return (
    <fieldset className="event-types">
      <legend>Events</legend>
      {WEBHOOK_EVENT_TYPES.map((type) => (
        <label key={type} className="inline">
          <input
            type="checkbox"
            disabled={disabled}
            checked={value.includes(type)}
            onChange={(e) => onChange(e.target.checked ? [...value, type] : value.filter((t) => t !== type))}
          />{' '}
          <code>{type}</code> <span className="muted">{EVENT_TYPE_HELP[type]}</span>
        </label>
      ))}
    </fieldset>
  )
}

/** What happened to a delivery, in words (Feature 011). */
function describeDelivery(d: WebhookDelivery) {
  const error = d.last_error_message ?? words(d.last_error_code)
  switch (d.state) {
    case 'delivered':
      return `Delivered ${formatDate(d.delivered_at)} (attempt ${d.attempt_in_generation} of ${d.max_attempts}).`
    case 'retry_wait':
      return `Attempt ${d.attempt_in_generation} of ${d.max_attempts} failed: ${error}. Next attempt at ${formatDate(d.next_attempt_at)}.`
    case 'pending':
      return d.attempt_in_generation === 0 ? 'Queued; it is sent while the endpoint is active.' : `Queued for attempt ${d.attempt_in_generation + 1} of ${d.max_attempts}.`
    case 'running':
      return `Attempt ${d.attempt_in_generation} of ${d.max_attempts} is being sent.`
    case 'failed':
      return d.last_outcome === 'attempts_exhausted' || d.last_error_code === 'outcome_unknown'
        ? `Automatic attempts exhausted (${d.attempt_in_generation} of ${d.max_attempts}). Last error: ${error}. Fix the receiver and retry.`
        : `Failed without automatic retry: ${error}. The receiver rejected the request or answered with a redirect, so repeating it unchanged would not help. Fix the receiver or the URL, then retry.`
    case 'held':
      return `Held: ${HOLDS[d.hold_reason ?? ''] ?? words(d.hold_reason)}`
    case 'abandoned':
      return `Abandoned: ${d.abandon_reason ?? '—'}. Requests already sent cannot be recalled.`
  }
}

/**
 * Feature 010: the tenant's single payment-status webhook endpoint, its signing keys, and its
 * delivery history. Nothing here changes a payment.
 */
export function Webhooks({
  prefix,
  owner,
  readOnly,
  canManageChildren,
}: {
  prefix: string
  owner: Tenant
  readOnly: boolean
  canManageChildren: boolean
}) {
  const [endpoint, setEndpoint] = useState<WebhookEndpoint | null | undefined>(undefined)
  const [secret, setSecret] = useState<{ key_id: string; secret: string } | null>(null)
  const [reload, setReload] = useState(0)
  const { error, run } = useAction()
  const base = `${prefix}/webhook-endpoint`

  const load = useCallback(async () => {
    try {
      setEndpoint(await api<WebhookEndpoint>('GET', base))
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) setEndpoint(null)
      else throw e
    }
  }, [base])

  useEffect(() => {
    void run(load)
  }, [run, load])

  const changed = (next: WebhookEndpoint) => {
    setEndpoint(next)
    setReload((n) => n + 1)
  }

  return (
    <>
      <Section title="Payment webhook">
        <p className="muted">
          Pairrot sends signed events to this URL: by default <code>payment_request.updated</code> whenever a payment request’s
          paid amount or status changes, and optionally receipts, review cases, and address pauses. Failed requests are retried
          after 5 s, 60 s, and 1 h. See <code>docs/webhooks-receiver.md</code> for the receiver contract.
        </p>
        {secret && <WebhookSecret secret={secret} onDismiss={() => setSecret(null)} />}
        <ErrorText error={error} />
        {endpoint === null && !readOnly && (
          <CreateEndpoint
            base={base}
            canManageChildren={canManageChildren && owner.capabilities.can_create_subtenants}
            onCreated={(signed) => {
              if (signed.signing_secret) setSecret(signed.signing_secret)
              changed(signed.endpoint)
            }}
          />
        )}
        {endpoint === null && readOnly && <p className="muted">No webhook endpoint.</p>}
        {endpoint && (
          <EndpointSettings
            base={base}
            endpoint={endpoint}
            readOnly={readOnly}
            canManageChildren={canManageChildren && owner.capabilities.can_create_subtenants}
            onChanged={changed}
            onSecret={setSecret}
          />
        )}
      </Section>
      {endpoint && <Deliveries key={`d-${reload}`} prefix={prefix} endpoint={endpoint} readOnly={readOnly} onChanged={() => void run(load)} />}
      {endpoint && <Events key={`e-${reload}`} prefix={prefix} endpoint={endpoint} readOnly={readOnly} onReplayed={() => changed(endpoint)} />}
    </>
  )
}

/** The signing secret, shown once from memory only; never stored in the browser. */
function WebhookSecret({ secret, onDismiss }: { secret: { key_id: string; secret: string }; onDismiss: () => void }) {
  const [copied, setCopied] = useState(false)
  return (
    <div className="secret">
      <p>
        <strong>Copy this signing secret now and install it in the receiver.</strong> It is shown only once; Pairrot never
        displays it again. If it is lost, rotate the secret. Key ID: <code>{secret.key_id}</code>
      </p>
      <code className="secret-value">{secret.secret}</code>
      <div className="row">
        <button
          onClick={async () => {
            await navigator.clipboard.writeText(secret.secret)
            setCopied(true)
          }}
        >
          {copied ? 'Copied' : 'Copy'}
        </button>
        <button className="secondary" onClick={onDismiss}>
          Done
        </button>
      </div>
    </div>
  )
}

function CreateEndpoint({
  base,
  canManageChildren,
  onCreated,
}: {
  base: string
  canManageChildren: boolean
  onCreated: (signed: SignedWebhookEndpoint) => void
}) {
  const [form, setForm] = useState<{ url: string; environment: 'live' | 'test'; scope: WebhookScope; eventTypes: WebhookEventType[] }>({
    url: '',
    environment: 'live',
    scope: 'own',
    eventTypes: ['payment_request.updated'],
  })
  const key = useIdempotencyKey()
  const { busy, error, run, setError } = useAction()
  const edit = (next: typeof form) => {
    key.reset()
    setError(null)
    setForm(next)
  }

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault()
        void run(async () => {
          const signed = await api<SignedWebhookEndpoint>(
            'POST',
            base,
            { url: form.url.trim(), environment: form.environment, scope: form.scope, event_types: form.eventTypes },
            { idempotencyKey: key.current() },
          )
          key.reset()
          onCreated(signed)
        })
      }}
    >
      <label>
        URL (HTTPS)
        <input value={form.url} placeholder="https://example.com/pairrot/webhook" onChange={(e) => edit({ ...form, url: e.target.value })} required />
      </label>
      <div className="row">
        <label className="inline">
          Environment{' '}
          <select value={form.environment} onChange={(e) => edit({ ...form, environment: e.target.value as 'live' | 'test' })}>
            <option value="live">live</option>
            <option value="test">test</option>
          </select>
        </label>
        {canManageChildren && (
          <label className="inline">
            Scope{' '}
            <select value={form.scope} onChange={(e) => edit({ ...form, scope: e.target.value as WebhookScope })}>
              <option value="own">this tenant only</option>
              <option value="own_and_direct_children">this tenant and its direct subtenants</option>
            </select>
          </label>
        )}
        <button type="submit" disabled={busy || form.eventTypes.length === 0}>
          Create endpoint
        </button>
      </div>
      <EventTypePicker value={form.eventTypes} onChange={(eventTypes) => edit({ ...form, eventTypes })} />
      <p className="muted">The endpoint starts paused. Install the secret in the receiver, send a test, then activate it.</p>
      <ErrorText error={error} />
    </form>
  )
}

function EndpointSettings({
  base,
  endpoint: e,
  readOnly,
  canManageChildren,
  onChanged,
  onSecret,
}: {
  base: string
  endpoint: WebhookEndpoint
  readOnly: boolean
  canManageChildren: boolean
  onChanged: (next: WebhookEndpoint) => void
  onSecret: (secret: { key_id: string; secret: string }) => void
}) {
  const [url, setUrl] = useState(e.url ?? '')
  const [eventTypes, setEventTypes] = useState<WebhookEventType[]>(e.event_types)
  const [grace, setGrace] = useState('86400')
  const [revokeRetiring, setRevokeRetiring] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const rotateKey = useIdempotencyKey()
  const testKey = useIdempotencyKey()
  const { busy, error, run } = useAction()
  const health = HEALTH[e.health.status]
  const manageable = !readOnly && (e.scope === 'own' || canManageChildren)

  const patch = (body: Record<string, unknown>, message: string) =>
    run(async () => {
      try {
        onChanged(await api<WebhookEndpoint>('PATCH', base, body, { ifMatch: e.version }))
        setNotice(message)
      } catch (err) {
        if (err instanceof ApiError && err.status === 412) onChanged(await api<WebhookEndpoint>('GET', base))
        throw err
      }
    })

  return (
    <div>
      <h3>
        Endpoint <span className={`badge badge-${health.tone}`}>{health.label}</span>
      </h3>
      <p className="muted">{health.help}</p>
      <dl>
        <dt>Destination</dt>
        <dd>
          {e.url ? <code>{e.url}</code> : <span className="badge badge-deleted">erased</span>}{' '}
          <span className="muted">revision {e.destination_revision}</span>
        </dd>
        <dt>Scope</dt>
        <dd>{e.scope === 'own' ? 'this tenant only' : 'this tenant and its direct subtenants'}</dd>
        <dt>Environment</dt>
        <dd>{e.environment}</dd>
        <dt>Events</dt>
        <dd>
          {e.event_types.map((t) => (
            <code key={t}>{t} </code>
          ))}
        </dd>
        <dt>Last test accepted</dt>
        <dd>{formatDate(e.health.last_test_accepted_at)}</dd>
        <dt>Last real delivery</dt>
        <dd>{formatDate(e.health.last_success_at)}</dd>
        <dt>Deliveries</dt>
        <dd>
          {e.deliveries.pending} pending, {e.deliveries.running} running, {e.deliveries.retry_wait} waiting for retry, {e.deliveries.failed} failed,{' '}
          {e.deliveries.held} held, {e.deliveries.delivered} delivered, {e.deliveries.abandoned} abandoned
        </dd>
      </dl>

      {!manageable && e.scope !== 'own' && !readOnly && (
        <p className="muted">This endpoint includes subtenants; only a parent administrator or a management key can change it.</p>
      )}

      {manageable && (
        <>
          <div className="row">
            <button disabled={busy} onClick={() => void patch({ state: e.state === 'active' ? 'paused' : 'active' }, e.state === 'active' ? 'Paused; changes accumulate.' : 'Activated; the backlog is sent.')}>
              {e.state === 'active' ? 'Pause' : 'Activate'}
            </button>
            <button
              className="secondary"
              disabled={busy}
              onClick={() =>
                void run(async () => {
                  await api('POST', `${base}/test`, undefined, { idempotencyKey: testKey.current() })
                  testKey.reset()
                  setNotice('A signed webhook.test was queued (also while paused). It has no payment effect; check the delivery list.')
                })
              }
            >
              Send test
            </button>
            {canManageChildren && (
              <button
                className="secondary"
                disabled={busy}
                onClick={() =>
                  void patch(
                    { scope: e.scope === 'own' ? 'own_and_direct_children' : 'own' },
                    e.scope === 'own' ? 'Subtenants’ future changes are included. Older history is sent only by replay.' : 'Subtenants’ queued deliveries are held.',
                  )
                }
              >
                {e.scope === 'own' ? 'Include direct subtenants' : 'Only this tenant'}
              </button>
            )}
          </div>

          <form
            className="row"
            onSubmit={(ev) => {
              ev.preventDefault()
              if (!confirm('Changing the URL creates a new destination revision. Queued deliveries are held and are not sent to the new URL unless you replay them. Continue?')) return
              void patch({ url: url.trim() }, 'URL changed. Queued deliveries for the old destination are held; replay them to send them here.')
            }}
          >
            <input value={url} onChange={(ev) => setUrl(ev.target.value)} required />
            <button type="submit" className="secondary" disabled={busy || url.trim() === e.url}>
              Change URL
            </button>
          </form>

          <form
            onSubmit={(ev) => {
              ev.preventDefault()
              void patch({ event_types: eventTypes }, 'Subscription changed. It applies to events recorded from now on; replay older ones if needed.')
            }}
          >
            <EventTypePicker value={eventTypes} onChange={setEventTypes} disabled={busy} />
            <button
              type="submit"
              className="secondary"
              disabled={busy || eventTypes.length === 0 || [...eventTypes].sort().join() === [...e.event_types].sort().join()}
            >
              Save events
            </button>
          </form>
        </>
      )}

      <h4>Signing keys</h4>
      <table>
        <thead>
          <tr>
            <th>Key ID</th>
            <th>State</th>
            <th>Created</th>
            <th>Retires</th>
            <th>Retired</th>
          </tr>
        </thead>
        <tbody>
          {e.signing_keys.map((k) => (
            <tr key={k.key_id} className={k.state === 'retired' ? 'revoked' : ''}>
              <td>
                <code>{k.key_id}</code>
              </td>
              <td>{k.state}</td>
              <td>{formatDate(k.created_at)}</td>
              <td>{formatDate(k.retires_at)}</td>
              <td>{formatDate(k.retired_at)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {manageable && (
        <form
          className="row"
          onSubmit={(ev) => {
            ev.preventDefault()
            void run(async () => {
              const signed = await api<SignedWebhookEndpoint>(
                'POST',
                `${base}/rotate-secret`,
                { expected_version: e.version, grace_period_seconds: Number(grace), revoke_retiring: revokeRetiring },
                { idempotencyKey: rotateKey.current() },
              )
              rotateKey.reset()
              if (signed.signing_secret) onSecret(signed.signing_secret)
              onChanged(signed.endpoint)
              setNotice(Number(grace) === 0 ? 'Rotated; the previous key no longer signs.' : 'Rotated; both keys sign until the previous one retires.')
            })
          }}
        >
          <label className="inline">
            Previous key keeps signing for{' '}
            <select
              value={grace}
              onChange={(ev) => {
                rotateKey.reset()
                setGrace(ev.target.value)
              }}
            >
              <option value="86400">24 hours</option>
              <option value="3600">1 hour</option>
              <option value="0">0 (revoke now)</option>
            </select>
          </label>
          <label className="inline">
            <input
              type="checkbox"
              checked={revokeRetiring}
              onChange={(ev) => {
                rotateKey.reset()
                setRevokeRetiring(ev.target.checked)
              }}
            />{' '}
            revoke a key still retiring
          </label>
          <button type="submit" className="secondary" disabled={busy}>
            Rotate secret
          </button>
        </form>
      )}
      {notice && <p className="muted">{notice}</p>}
      <ErrorText error={error} />
    </div>
  )
}

function Deliveries({ prefix, endpoint, readOnly, onChanged }: { prefix: string; endpoint: WebhookEndpoint; readOnly: boolean; onChanged: () => void }) {
  const [items, setItems] = useState<WebhookDelivery[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [state, setState] = useState<'' | WebhookDeliveryState>('')
  const [openId, setOpenId] = useState<string | null>(null)
  const { busy, error, run } = useAction()

  const loadPage = useCallback(
    async (after: string | null) => {
      const query = new URLSearchParams({ limit: '50' })
      if (state) query.set('state', state)
      if (after) query.set('cursor', after)
      const page = await api<ListResponse<WebhookDelivery>>('GET', `${prefix}/webhook-deliveries?${query}`)
      setItems((current) => (after ? [...current, ...page.items] : page.items))
      setCursor(page.next_cursor)
    },
    [prefix, state],
  )

  useEffect(() => {
    void run(() => loadPage(null))
  }, [run, loadPage])

  return (
    <Section title="Webhook deliveries">
      <div className="row">
        <label className="inline">
          State{' '}
          <select value={state} onChange={(e) => setState(e.target.value as '' | WebhookDeliveryState)}>
            <option value="">all</option>
            {STATES.map((s) => (
              <option key={s} value={s}>
                {words(s)}
              </option>
            ))}
          </select>
        </label>
        <button className="secondary" disabled={busy} onClick={() => void run(() => loadPage(null))}>
          Refresh
        </button>
      </div>
      <ErrorText error={error} />
      <table>
        <thead>
          <tr>
            <th>Created</th>
            <th>State</th>
            <th>What happened</th>
            <th>Owner</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {items.map((d) => (
            <tr key={d.id}>
              <td>{formatDate(d.created_at)}</td>
              <td>
                <span className={`badge badge-${d.state === 'delivered' ? 'active' : d.state === 'failed' || d.state === 'held' ? 'suspended' : 'pending'}`}>{words(d.state)}</span>
              </td>
              <td>{describeDelivery(d)}</td>
              <td>{d.owner_tenant_id === endpoint.tenant_id ? 'this tenant' : <code>{d.owner_tenant_id}</code>}</td>
              <td>
                <button className="secondary" onClick={() => setOpenId(openId === d.id ? null : d.id)}>
                  {openId === d.id ? 'Close' : 'Detail'}
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {items.length === 0 && <p className="muted">No deliveries.</p>}
      {cursor && (
        <button className="secondary" disabled={busy} onClick={() => void run(() => loadPage(cursor))}>
          Load more
        </button>
      )}
      {openId && (
        <DeliveryDetail
          key={openId}
          path={`${prefix}/webhook-deliveries/${openId}`}
          endpoint={endpoint}
          readOnly={readOnly}
          onChanged={(d) => {
            setItems((current) => current.map((x) => (x.id === d.id ? d : x)))
            onChanged()
          }}
        />
      )}
    </Section>
  )
}

function DeliveryDetail({
  path,
  endpoint,
  readOnly,
  onChanged,
}: {
  path: string
  endpoint: WebhookEndpoint
  readOnly: boolean
  onChanged: (d: WebhookDelivery) => void
}) {
  const [detail, setDetail] = useState<WebhookDeliveryDetail | null>(null)
  const [reason, setReason] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const key = useIdempotencyKey()
  const { busy, error, run } = useAction()

  const load = useCallback(async () => setDetail(await api<WebhookDeliveryDetail>('GET', path)), [path])

  useEffect(() => {
    void run(load)
  }, [run, load])

  if (!detail) return <ErrorText error={error} />
  const d = detail.delivery
  const manageable = !readOnly

  const act = (suffix: 'retry' | 'abandon', message: string) =>
    run(async () => {
      try {
        const next = await api<WebhookDeliveryDetail>('POST', `${path}/${suffix}`, { expected_version: d.version, reason: reason.trim() }, {
          idempotencyKey: key.current(),
        })
        key.reset()
        setDetail(next)
        onChanged(next.delivery)
        setReason('')
        setNotice(message)
      } catch (e) {
        if (e instanceof ApiError && e.status === 412) {
          key.reset()
          await load()
        }
        throw e
      }
    })

  return (
    <div className="connection">
      <h3>Delivery {d.id}</h3>
      <p>{describeDelivery(d)}</p>
      <dl>
        <dt>Generation</dt>
        <dd>
          {d.generation} · {d.attempt_in_generation} of {d.max_attempts} attempts used · {d.total_attempts} attempts in total
        </dd>
        <dt>Destination</dt>
        <dd>
          revision {d.destination_revision}
          {d.destination_revision !== endpoint.destination_revision && (
            <span className="error"> (the endpoint is now at revision {endpoint.destination_revision}: {detail.current_url ?? 'erased'})</span>
          )}
        </dd>
      </dl>

      <h4>Attempts</h4>
      <table>
        <thead>
          <tr>
            <th>Generation</th>
            <th>Attempt</th>
            <th>Started</th>
            <th>Outcome</th>
            <th>HTTP</th>
            <th>Error</th>
            <th>Duration</th>
            <th>Destination</th>
          </tr>
        </thead>
        <tbody>
          {detail.attempts.map((a) => (
            <tr key={a.id}>
              <td>{a.generation}</td>
              <td>
                {a.attempt_number} of {d.max_attempts}
              </td>
              <td>{formatDate(a.started_at)}</td>
              <td>{words(a.outcome)}</td>
              <td>{a.http_status ?? '—'}</td>
              <td>
                {words(a.error_code)}
                {a.response_excerpt && <div className="muted">{a.response_excerpt}</div>}
              </td>
              <td>{a.duration_ms === null ? '—' : `${a.duration_ms} ms`}</td>
              <td>
                r{a.destination_revision} {a.url ? <code>{a.url}</code> : <span className="muted">erased</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {detail.attempts.length === 0 && <p className="muted">No attempt yet.</p>}

      <details>
        <summary>
          Event {detail.event.type} {detail.event.sequence !== null && `· sequence ${detail.event.sequence}`}
        </summary>
        {detail.event.payload_erased_at ? (
          <p className="muted">
            The payload was erased with its tenant on {formatDate(detail.event.payload_erased_at)}; it cannot be sent again. Digest{' '}
            <code>{detail.event.body_sha256}</code>.
          </p>
        ) : (
          <pre className="source">{JSON.stringify(detail.event.body, null, 2)}</pre>
        )}
      </details>

      {manageable && (d.state === 'failed' || d.state === 'held' || d.state === 'pending' || d.state === 'retry_wait') && (
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault()
          }}
        >
          <input
            placeholder="Reason (required)"
            value={reason}
            onChange={(e) => {
              key.reset()
              setReason(e.target.value)
            }}
          />
          {(d.state === 'failed' || d.state === 'held') && d.hold_reason !== 'destination_changed' && (
            <button type="button" disabled={busy || reason.trim() === ''} onClick={() => void act('retry', 'A new generation with up to four attempts was queued.')}>
              Retry
            </button>
          )}
          <button
            type="button"
            className="danger"
            disabled={busy || reason.trim() === ''}
            onClick={() => {
              if (!confirm('Abandon this delivery? No further attempts are made; requests already sent cannot be recalled.')) return
              void act('abandon', 'Abandoned.')
            }}
          >
            Abandon
          </button>
        </form>
      )}
      {d.hold_reason === 'destination_changed' && <p className="muted">Use “Replay” in the event list to send it to the current destination.</p>}
      {notice && <p className="muted">{notice}</p>}
      <ErrorText error={error} />
    </div>
  )
}

/** Payment events in the caller's scope; selected ones can be replayed to the current destination. */
function Events({ prefix, endpoint, readOnly, onReplayed }: { prefix: string; endpoint: WebhookEndpoint; readOnly: boolean; onReplayed: () => void }) {
  const [items, setItems] = useState<WebhookEvent[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [reason, setReason] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const [type, setType] = useState<WebhookEventType | ''>('')
  const key = useIdempotencyKey()
  const { busy, error, run } = useAction()

  const loadPage = useCallback(
    async (after: string | null) => {
      const query = new URLSearchParams({ limit: '50' })
      if (after) query.set('cursor', after)
      if (type) query.set('type', type)
      const page = await api<ListResponse<WebhookEvent>>('GET', `${prefix}/webhook-events?${query}`)
      setItems((current) => (after ? [...current, ...page.items] : page.items))
      setCursor(page.next_cursor)
    },
    [prefix, type],
  )

  useEffect(() => {
    void run(() => loadPage(null))
  }, [run, loadPage])

  const toggle = (id: string) => {
    key.reset()
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id)
      else if (next.size < 100) next.add(id)
      return next
    })
  }

  return (
    <Section title="Events">
      <p className="muted">
        Every recorded event, whether or not the endpoint subscribes to its type. Creating or widening an endpoint does not send
        old history; select events to replay them explicitly (up to 100 at once, subscribed types only).
      </p>
      <label className="inline">
        Type{' '}
        <select
          value={type}
          onChange={(e) => {
            setSelected(new Set())
            setType(e.target.value as WebhookEventType | '')
          }}
        >
          <option value="">all</option>
          {WEBHOOK_EVENT_TYPES.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </label>
      <ErrorText error={error} />
      <table>
        <thead>
          <tr>
            <th />
            <th>Created</th>
            <th>Type</th>
            <th>Subject</th>
            <th>Sequence</th>
            <th>Owner</th>
          </tr>
        </thead>
        <tbody>
          {items.map((ev) => (
            <tr key={ev.id}>
              <td>
                {!readOnly && !ev.synthetic && endpoint.event_types.includes(ev.type as WebhookEventType) && (
                  <input type="checkbox" checked={selected.has(ev.id)} onChange={() => toggle(ev.id)} aria-label="Select for replay" />
                )}
              </td>
              <td>{formatDate(ev.created_at)}</td>
              <td>
                {ev.type} {ev.environment === 'test' && <span className="badge badge-pending">test</span>}
              </td>
              <td>{ev.subject_id ? <code>{ev.subject_id}</code> : '—'}</td>
              <td>{ev.sequence ?? '—'}</td>
              <td>{ev.tenant_id === endpoint.tenant_id ? 'this tenant' : <code>{ev.tenant_id}</code>}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {items.length === 0 && <p className="muted">No events.</p>}
      {cursor && (
        <button className="secondary" disabled={busy} onClick={() => void run(() => loadPage(cursor))}>
          Load more
        </button>
      )}
      {!readOnly && selected.size > 0 && (
        <form
          onSubmit={(e) => {
            e.preventDefault()
            if (!confirm(`Send ${selected.size} event(s) to ${endpoint.url} (revision ${endpoint.destination_revision})?`)) return
            void run(async () => {
              await api(
                'POST',
                `${prefix}/webhook-endpoint/replay`,
                { event_ids: [...selected], expected_destination_revision: endpoint.destination_revision, reason: reason.trim() },
                { idempotencyKey: key.current() },
              )
              key.reset()
              setSelected(new Set())
              setReason('')
              setNotice('Queued. Each event keeps its ID, body, and sequence.')
              onReplayed()
            })
          }}
        >
          <p>
            Replay {selected.size} event(s) to <code>{endpoint.url}</code> (destination revision {endpoint.destination_revision}).
          </p>
          <div className="row">
            <input
              placeholder="Reason (required)"
              value={reason}
              onChange={(e) => {
                key.reset()
                setReason(e.target.value)
              }}
              required
            />
            <button type="submit" disabled={busy || reason.trim() === ''}>
              Replay
            </button>
          </div>
        </form>
      )}
      {notice && <p className="muted">{notice}</p>}
    </Section>
  )
}
