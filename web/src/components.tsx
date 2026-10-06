import { useState, type ReactNode } from 'react'
import type { IssuedApiKey, TenantStatus } from './api'
import { errorMessage } from './util'

export function ErrorText({ error }: { error: unknown }) {
  return error ? <p className="error">{errorMessage(error)}</p> : null
}

export function StatusBadge({ status, blocked }: { status: TenantStatus; blocked?: boolean }) {
  const label = status === 'active' && blocked ? 'parent suspended' : status
  return <span className={`badge badge-${blocked ? 'suspended' : status}`}>{label}</span>
}

export function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="card">
      <h2>{title}</h2>
      {children}
    </section>
  )
}

/** Shows a newly issued key once. Dismissing it removes the secret from memory. */
export function SecretOnce({ issued, onDismiss }: { issued: IssuedApiKey; onDismiss: () => void }) {
  const [copied, setCopied] = useState(false)
  return (
    <div className="secret">
      <p>
        <strong>Copy this {issued.purpose} key now.</strong> It is shown only once; later you will see its metadata
        only. If it is lost, issue a new key and revoke this one.
      </p>
      <code className="secret-value">{issued.secret}</code>
      <div className="row">
        <button
          onClick={async () => {
            await navigator.clipboard.writeText(issued.secret)
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
