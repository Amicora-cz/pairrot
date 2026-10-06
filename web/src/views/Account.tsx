import { useState } from 'react'
import { api } from '../api'
import { ErrorText, Section } from '../components'
import { useAction } from '../util'

export function Account({ email, onSignedOut }: { email: string; onSignedOut: (message: string | null) => void }) {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const { busy, error, run } = useAction()

  return (
    <>
      <Section title="Change password">
        <form
          onSubmit={(e) => {
            e.preventDefault()
            void run(async () => {
              await api('POST', '/v1/auth/change-password', { current_password: current, new_password: next })
              onSignedOut('Password changed. All sessions were signed out; sign in with the new password.')
            })
          }}
        >
          <input type="email" autoComplete="username" value={email} readOnly hidden />
          <label>
            Current password
            <input type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} required />
          </label>
          <label>
            New password (at least 12 characters)
            <input type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} required />
          </label>
          <button type="submit" disabled={busy}>
            Change password
          </button>
        </form>
        <p className="muted">Changing the password signs out every session. Tenant API keys keep working.</p>
      </Section>
      <Section title="Sign out">
        <div className="row">
          <button
            disabled={busy}
            onClick={() =>
              void run(async () => {
                await api('POST', '/v1/auth/logout')
                onSignedOut('You are signed out.')
              })
            }
          >
            Sign out
          </button>
          <button
            className="secondary"
            disabled={busy}
            onClick={() =>
              void run(async () => {
                await api('POST', '/v1/auth/logout-all')
                onSignedOut('You are signed out on every device.')
              })
            }
          >
            Sign out everywhere
          </button>
        </div>
      </Section>
      <ErrorText error={error} />
    </>
  )
}
