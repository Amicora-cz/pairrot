import type { Membership } from '../api'
import { ErrorText, Section, StatusBadge } from '../components'
import { useAction } from '../util'

export function TenantPicker({ memberships, onSelect }: { memberships: Membership[]; onSelect: (tenantId: string) => Promise<void> }) {
  const { busy, error, run } = useAction()

  return (
    <Section title="Select a tenant">
      {memberships.length === 0 ? (
        <p className="muted">
          You do not administer any tenant. Ask an operator to grant access with <code>Pairrot.Admin users grant</code>.
        </p>
      ) : (
        <ul className="list">
          {memberships.map((m) => (
            <li key={m.tenant.id}>
              <button disabled={busy} onClick={() => void run(() => onSelect(m.tenant.id))}>
                {m.tenant.configuration.name}
              </button>{' '}
              <code>{m.tenant.external_id}</code> <StatusBadge status={m.tenant.status} blocked={m.blocked} />
              {m.tenant.parent_tenant_id === null && <span className="muted"> root</span>}
            </li>
          ))}
        </ul>
      )}
      <ErrorText error={error} />
    </Section>
  )
}
