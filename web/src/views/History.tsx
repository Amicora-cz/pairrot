import { useCallback, useEffect, useState } from 'react'
import { api, type AuditEvent, type ListResponse } from '../api'
import { ErrorText, Section } from '../components'
import { formatDate, useAction } from '../util'

function actorLabel(actor: AuditEvent['actor']) {
  switch (actor.type) {
    case 'user':
      return `user ${actor.email ?? actor.user_id}`
    case 'api_key':
      return `API key ${actor.api_key_id}`
    default:
      return `admin: ${actor.description}`
  }
}

/** Change history, newest first. */
export function History({ path, title = 'History' }: { path: string; title?: string }) {
  const [events, setEvents] = useState<AuditEvent[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const { busy, error, run } = useAction()

  const loadPage = useCallback(
    async (after: string | null) => {
      const page = await api<ListResponse<AuditEvent>>('GET', `${path}?limit=50${after ? `&cursor=${after}` : ''}`)
      setEvents((current) => (after ? [...current, ...page.items] : page.items))
      setCursor(page.next_cursor)
    },
    [path],
  )

  useEffect(() => {
    void run(() => loadPage(null))
  }, [run, loadPage])

  return (
    <Section title={title}>
      <ErrorText error={error} />
      <table>
        <thead>
          <tr>
            <th>When</th>
            <th>Action</th>
            <th>By</th>
            <th>Details</th>
          </tr>
        </thead>
        <tbody>
          {events.map((event) => (
            <tr key={event.id}>
              <td>{formatDate(event.created_at)}</td>
              <td>
                <code>{event.action}</code>
              </td>
              <td>{actorLabel(event.actor)}</td>
              <td>
                <code className="details">{JSON.stringify(event.details)}</code>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {cursor && (
        <button className="secondary" disabled={busy} onClick={() => void run(() => loadPage(cursor))}>
          Load older
        </button>
      )}
    </Section>
  )
}
