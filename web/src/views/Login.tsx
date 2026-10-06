import { useState } from 'react'
import { api, type SessionSummary } from '../api'
import { ErrorText } from '../components'
import { useAction } from '../util'

export function Login({ notice, onSignedIn }: { notice: string | null; onSignedIn: (summary: SessionSummary) => Promise<void> }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const { busy, error, run } = useAction()

  return (
    <main className="centered">
      <form
        className="card login"
        onSubmit={(e) => {
          e.preventDefault()
          void run(async () => {
            const summary = await api<SessionSummary>('POST', '/v1/auth/login', { email, password }, { silent: true })
            setPassword('')
            await onSignedIn(summary)
          })
        }}
      >
        <h1>Pairrot</h1>
        <p className="muted">Local test client. Sign in with an account created by Pairrot.Admin.</p>
        {notice && <p className="notice">{notice}</p>}
        <label>
          Email
          <input type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} required />
        </label>
        <label>
          Password
          <input
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
        </label>
        <ErrorText error={error} />
        <button type="submit" disabled={busy}>
          Sign in
        </button>
      </form>
    </main>
  )
}
