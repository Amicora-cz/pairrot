import { useCallback, useEffect, useState } from 'react'
import { api, type ApiKey, type IssuedApiKey, type ListResponse } from '../api'
import { ErrorText, SecretOnce, Section } from '../components'
import { formatDate, useAction } from '../util'

/** The selected tenant's own keys. */
export function ApiKeys({ canIssueManagement }: { canIssueManagement: boolean }) {
  return (
    <Section title="API keys">
      <KeyManager basePath="/v1/tenant/api-keys" purposes={canIssueManagement ? ['tenant', 'management'] : ['tenant']} />
    </Section>
  )
}

/** Lists keys (metadata only), issues new ones showing the secret once, and revokes keys. */
export function KeyManager({ basePath, purposes }: { basePath: string; purposes: ('tenant' | 'management')[] }) {
  const [keys, setKeys] = useState<ApiKey[]>([])
  const [purpose, setPurpose] = useState<'tenant' | 'management'>('tenant')
  const [label, setLabel] = useState('')
  const [issued, setIssued] = useState<IssuedApiKey | null>(null)
  const { busy, error, run } = useAction()

  const reload = useCallback(async () => {
    setKeys((await api<ListResponse<ApiKey>>('GET', basePath)).items)
  }, [basePath])

  useEffect(() => {
    void run(reload)
  }, [run, reload])

  // Child keys are always tenant keys; the subtenant endpoint takes only a label.
  const ownKeys = basePath === '/v1/tenant/api-keys'

  return (
    <>
      {issued && <SecretOnce issued={issued} onDismiss={() => setIssued(null)} />}
      <form
        className="row"
        onSubmit={(e) => {
          e.preventDefault()
          void run(async () => {
            const body = ownKeys ? { purpose, label: label || null } : { label: label || null }
            setIssued(await api<IssuedApiKey>('POST', basePath, body))
            setLabel('')
            await reload()
          })
        }}
      >
        {ownKeys && (
          <select value={purpose} onChange={(e) => setPurpose(e.target.value as 'tenant' | 'management')}>
            {purposes.map((p) => (
              <option key={p} value={p}>
                {p} key
              </option>
            ))}
          </select>
        )}
        <input placeholder="Label (optional)" value={label} onChange={(e) => setLabel(e.target.value)} />
        <button type="submit" disabled={busy}>
          Issue key
        </button>
      </form>
      <ErrorText error={error} />
      <table>
        <thead>
          <tr>
            <th>Key ID</th>
            <th>Purpose</th>
            <th>Label</th>
            <th>Created</th>
            <th>Revoked</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {keys.map((key) => (
            <tr key={key.id} className={key.revoked_at ? 'revoked' : ''}>
              <td>
                <code>{key.id}</code>
              </td>
              <td>{key.purpose}</td>
              <td>{key.label ?? '—'}</td>
              <td>{formatDate(key.created_at)}</td>
              <td>{formatDate(key.revoked_at)}</td>
              <td>
                {!key.revoked_at && (
                  <button
                    className="danger"
                    disabled={busy}
                    onClick={() => {
                      if (!confirm(`Revoke key ${key.id}? Applications using it stop working immediately.`)) return
                      void run(async () => {
                        await api('DELETE', `${basePath}/${key.id}`)
                        await reload()
                      })
                    }}
                  >
                    Revoke
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  )
}
