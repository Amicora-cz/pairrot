import { useCallback, useEffect, useState } from 'react'
import { api, type BankConnection, type ForwardingSource, type ReceptionStatus, type SetupStatus } from '../api'
import { ErrorText } from '../components'
import { formatDate, useAction } from '../util'

const STATUS: Record<SetupStatus, { label: string; tone: string; help: string }> = {
  address_not_created: { label: 'no address', tone: 'pending', help: 'Create a notification address first.' },
  waiting_for_first_message: {
    label: 'waiting for the first notification',
    tone: 'pending',
    help: 'Nothing has arrived yet. Send yourself a small payment to test the setup.',
  },
  confirmation_received: {
    label: 'forwarding confirmation received',
    tone: 'pending',
    help: 'Your mail provider asks to confirm forwarding. Use the code below in your mailbox settings.',
  },
  bank_message_trusted: { label: 'working', tone: 'active', help: 'The latest bank notification was received and verified.' },
  bank_message_untrusted: {
    label: 'notification not verified',
    tone: 'suspended',
    help: 'A bank notification arrived, but neither the bank nor one of your forwarding mailboxes could be verified. Check that the mailbox below is the one that forwards, or use direct delivery.',
  },
  paused_foreign_mail: {
    label: 'paused: foreign mail',
    tone: 'suspended',
    help: 'This address received e-mails that are not bank notifications, so processing is paused. Check that your rule forwards only payment notifications.',
  },
}

const PATH_LABELS: Record<string, string> = {
  direct: 'directly from the bank',
  automatic_forward: 'by automatic forwarding',
  manual_forward: 'forwarded by hand',
}

/**
 * Feature 8 for one bank connection: whether notifications arrive and are verified, the
 * mailboxes allowed to forward them, and the forwarding guide. Pairrot never reads the mailbox;
 * it only sees what is forwarded.
 */
export function ForwardingPanel({
  path,
  connection,
  readOnly,
  onConnectionChanged,
}: {
  path: string
  connection: BankConnection
  readOnly: boolean
  onConnectionChanged: (c: BankConnection) => void
}) {
  const [status, setStatus] = useState<ReceptionStatus | null>(null)
  const [address, setAddress] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const { busy, error, run, setError } = useAction()

  const load = useCallback(async () => setStatus(await api<ReceptionStatus>('GET', `${path}/reception`)), [path])

  useEffect(() => {
    void run(load)
  }, [run, load])

  if (!status) return <ErrorText error={error} />
  const info = STATUS[status.setup_status]
  const paused = status.address_processing === 'paused_foreign_mail'

  return (
    <div className="forwarding">
      <h4>
        Bank notifications <span className={`badge badge-${info.tone}`}>{info.label}</span>
      </h4>
      <p className="muted">{info.help}</p>

      {paused && (
        <div className="stub-banner">
          <strong>Processing is paused since {formatDate(status.paused_at)}.</strong> {status.foreign_messages_in_window} foreign e-mail(s) in
          the last 24 hours from {status.recent_foreign_domains.join(', ') || 'unknown senders'}. Their content was not stored.
          {!readOnly && (
            <div className="row">
              {!status.operator_resume_required && (
                <button
                  disabled={busy}
                  onClick={() =>
                    void run(async () => {
                      setStatus(await api<ReceptionStatus>('POST', `${path}/inbound-address/resume`))
                      setNotice('Processing resumed. E-mails received while paused are not processed.')
                    })
                  }
                >
                  I fixed the rule, resume
                </button>
              )}
              <button
                className="secondary"
                disabled={busy}
                onClick={() =>
                  void run(async () => {
                    if (!confirm('Get a new address? The old one stops working and you must update your forwarding rule or bank settings.')) return
                    onConnectionChanged(await api<BankConnection>('POST', `${path}/inbound-address`))
                    await load()
                    setNotice('A new address was created. Update your forwarding rule or bank settings to use it.')
                  })
                }
              >
                Get a new address
              </button>
            </div>
          )}
          {status.operator_resume_required && (
            <p>The address was paused again soon after it was resumed. Get a new address, or ask support to resume it.</p>
          )}
        </div>
      )}

      {!paused && status.foreign_messages_in_window > 0 && (
        <p className="error">
          {status.foreign_messages_in_window} e-mail(s) in the last 24 hours were not bank notifications (
          {status.recent_foreign_domains.join(', ')}). They were not stored. Too many of them pause the address.
        </p>
      )}

      {status.confirmation && (
        <div className="stub-banner">
          <strong>Forwarding confirmation from {status.confirmation.sender_domain ?? 'your mail provider'}</strong>, received{' '}
          {formatDate(status.confirmation.received_at)}.
          {status.confirmation.code ? (
            <p>
              Confirmation code: <code className="code-large">{status.confirmation.code}</code>
            </p>
          ) : (
            <p>No code was found; read the message below and finish the setup in your mailbox.</p>
          )}
          <details>
            <summary>Message text (links are not opened by Pairrot)</summary>
            <pre className="source">{status.confirmation.excerpt}</pre>
          </details>
        </div>
      )}

      {status.latest_bank_message && (
        <p className="muted">
          Latest notification {formatDate(status.latest_bank_message.received_at)}
          {status.latest_bank_message.origin_path && `, ${PATH_LABELS[status.latest_bank_message.origin_path] ?? status.latest_bank_message.origin_path}`}
          {status.latest_bank_message.forwarding_source && ` (${status.latest_bank_message.forwarding_source})`}:{' '}
          {status.latest_bank_message.trust_decision === 'trusted' ? 'verified' : 'not verified'}
          {status.latest_bank_message.parse_outcome && `, ${status.latest_bank_message.parse_outcome.replaceAll('_', ' ')}`}.
        </p>
      )}

      <h5>Forwarding mailboxes</h5>
      {status.forwarding_sources.length === 0 && <p className="muted">None. Add the mailbox that forwards the bank's notifications, e.g. your Gmail address.</p>}
      {status.forwarding_sources.map((s) => (
        <SourceRow key={s.id} path={path} source={s} readOnly={readOnly} onChanged={() => run(load)} />
      ))}
      {!readOnly && (
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault()
            void run(async () => {
              const created = await api<ForwardingSource>('POST', `${path}/forwarding-sources`, { address: address.trim() })
              setAddress('')
              setNotice(`Notifications forwarded from ${created.address} are now accepted.`)
              await load()
            })
          }}
        >
          <input
            placeholder="your-mailbox@gmail.com"
            value={address}
            onChange={(e) => {
              setError(null)
              setAddress(e.target.value)
            }}
          />
          <button type="submit" className="secondary" disabled={busy || address.trim() === ''}>
            Add forwarding mailbox
          </button>
        </form>
      )}
      {notice && <p className="muted">{notice}</p>}
      <ErrorText error={error} />

      <ForwardingGuide address={status.inbound_address ?? connection.inbound_address} />
    </div>
  )
}

function SourceRow({ path, source: s, readOnly, onChanged }: { path: string; source: ForwardingSource; readOnly: boolean; onChanged: () => Promise<void> }) {
  const { busy, error, run } = useAction()

  return (
    <div className="row">
      <code>{s.address}</code>
      <span className="muted">added {formatDate(s.created_at)}</span>
      {!readOnly && (
        <button
          className="danger"
          disabled={busy}
          onClick={() =>
            void run(async () => {
              if (!confirm(`Stop accepting notifications forwarded from ${s.address}?`)) return
              await api('DELETE', `${path}/forwarding-sources/${s.id}`)
              await onChanged()
            })
          }
        >
          Remove
        </button>
      )}
      <ErrorText error={error} />
    </div>
  )
}

/** Česká spořitelna sends bank messages to one contact e-mail only, so forwarding is the usual setup. */
function ForwardingGuide({ address }: { address: string | null }) {
  const target = address ?? 'your Pairrot address'
  return (
    <details>
      <summary>How to set up notifications (Česká spořitelna)</summary>
      <ol>
        <li>
          In George, open Settings → <em>Informační zprávy</em> and activate <em>Příchozí platby</em>. The bank sends them to your
          contact e-mail.
        </li>
        <li>Add the mailbox that will forward the notifications above.</li>
        <li>
          In your mailbox, create a filter that forwards only payment notifications to <code>{target}</code>:
          <ul>
            <li>
              <strong>Gmail:</strong> Settings → Forwarding and POP/IMAP → Add a forwarding address <code>{target}</code>. Gmail sends
              a confirmation; its code appears here. Then create a filter <code>from:(ceskasporitelna@csas.cz) subject:("Přišla platba")</code>{' '}
              with the action “Forward to” this address. Do not forward all mail.
            </li>
            <li>
              <strong>Seznam:</strong> Settings → Filters → new filter for sender <code>ceskasporitelna@csas.cz</code> and subject{' '}
              <em>Přišla platba</em>, action “Forward to” <code>{target}</code>.
            </li>
          </ul>
        </li>
        <li>A single notification the filter missed can also be forwarded by hand from that mailbox.</li>
      </ol>
      <p className="muted">
        Alternatively, set <code>{target}</code> as the bank's message e-mail in George (direct delivery); then you stop receiving
        them yourself. Use one way only: direct delivery and forwarding together create duplicate copies that need review. E-mails
        that are not bank notifications are not stored, and too many of them pause the address.
      </p>
    </details>
  )
}
