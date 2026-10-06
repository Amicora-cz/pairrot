import { useEffect, useState } from 'react'
import { api } from '../api'
import { ErrorText, Section } from '../components'
import { useAction } from '../util'

interface ConfigurationResponse {
  name: string
  contact_email: string | null
}

export function Configuration({ onChanged }: { onChanged: () => Promise<void> }) {
  const [name, setName] = useState('')
  const [contactEmail, setContactEmail] = useState('')
  const [saved, setSaved] = useState(false)
  const load = useAction()
  const runLoad = load.run
  const save = useAction()

  useEffect(() => {
    void runLoad(async () => {
      const config = await api<ConfigurationResponse>('GET', '/v1/tenant/configuration')
      setName(config.name)
      setContactEmail(config.contact_email ?? '')
    })
  }, [runLoad])

  return (
    <Section title="Configuration">
      <form
        onSubmit={(e) => {
          e.preventDefault()
          setSaved(false)
          void save.run(async () => {
            const config = await api<ConfigurationResponse>('PATCH', '/v1/tenant/configuration', {
              name,
              contact_email: contactEmail.trim() === '' ? null : contactEmail,
            })
            setName(config.name)
            setContactEmail(config.contact_email ?? '')
            setSaved(true)
            await onChanged()
          })
        }}
      >
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} required />
        </label>
        <label>
          Contact email
          <input type="email" value={contactEmail} onChange={(e) => setContactEmail(e.target.value)} />
        </label>
        <p className="muted">The contact email is not a login and does not grant access to anyone.</p>
        <ErrorText error={load.error ?? save.error} />
        {saved && <p className="notice">Saved.</p>}
        <button type="submit" disabled={save.busy || load.busy}>
          Save
        </button>
      </form>
    </Section>
  )
}
