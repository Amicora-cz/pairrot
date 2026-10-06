import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, refreshAntiforgery, setAuthErrorListener, setExpectedTenant, type SessionSummary } from './api'
import { Account } from './views/Account'
import { ApiKeys } from './views/ApiKeys'
import { BankSettings } from './views/BankSettings'
import { Configuration } from './views/Configuration'
import { History } from './views/History'
import { Login } from './views/Login'
import { Subtenants } from './views/Subtenants'
import { TenantPicker } from './views/TenantPicker'
import { StatusBadge } from './components'

type Tab = 'configuration' | 'bank' | 'keys' | 'subtenants' | 'history' | 'account'

// Background polling only detects expiry; it is marked so it does not keep the session alive.
const POLL_MS = 60_000

export default function App() {
  const [summary, setSummary] = useState<SessionSummary | null>(null)
  const [phase, setPhase] = useState<'loading' | 'anonymous' | 'ready'>('loading')
  const [notice, setNotice] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('configuration')

  const applySummary = useCallback((next: SessionSummary) => {
    setExpectedTenant(next.selected_tenant_id)
    setSummary(next)
    setPhase('ready')
  }, [])

  const signedOut = useCallback((message: string | null) => {
    // Clear every sensitive view by dropping the whole signed-in tree.
    setExpectedTenant(null)
    setSummary(null)
    setPhase('anonymous')
    setNotice(message)
    void refreshAntiforgery()
  }, [])

  const loadSession = useCallback(async () => {
    try {
      applySummary(await api<SessionSummary>('GET', '/v1/auth/session', undefined, { silent: true }))
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) signedOut(null)
      else throw error
    }
  }, [applySummary, signedOut])

  useEffect(() => {
    setAuthErrorListener((error) => {
      if (error.status === 401) signedOut('Your session has ended. Sign in again.')
      else void loadSession() // Tenant access changed: fetch a fresh session summary.
    })
    void refreshAntiforgery().then(loadSession)
  }, [loadSession, signedOut])

  useEffect(() => {
    if (phase !== 'ready') return
    const timer = setInterval(async () => {
      try {
        applySummary(await api<SessionSummary>('GET', '/v1/auth/session', undefined, { background: true, silent: true }))
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) signedOut('Your session has expired. Sign in again.')
      }
    }, POLL_MS)
    return () => clearInterval(timer)
  }, [phase, applySummary, signedOut])

  if (phase === 'loading') return <main className="centered">Loading…</main>

  if (phase === 'anonymous' || !summary) {
    return (
      <Login
        notice={notice}
        onSignedIn={async (next) => {
          await refreshAntiforgery() // The token is bound to the new user.
          setNotice(null)
          setTab('configuration')
          applySummary(next)
        }}
      />
    )
  }

  const selected = summary.memberships.find((m) => m.tenant.id === summary.selected_tenant_id) ?? null
  const canManageChildren = !!selected && selected.tenant.parent_tenant_id === null && selected.tenant.capabilities.can_create_subtenants
  const activeTab = tab === 'subtenants' && !canManageChildren ? 'configuration' : tab

  const selectTenant = async (tenantId: string) => {
    applySummary(await api<SessionSummary>('POST', '/v1/auth/select-tenant', { tenant_id: tenantId }))
  }

  return (
    <div className="app">
      <header className="topbar">
        <strong>Pairrot · local test client</strong>
        <div className="topbar-tenant">
          {selected ? (
            <>
              <span>
                Tenant: <strong>{selected.tenant.configuration.name}</strong> <code>{selected.tenant.external_id}</code>
              </span>
              <StatusBadge status={selected.tenant.status} blocked={selected.blocked} />
            </>
          ) : (
            <span>No tenant selected</span>
          )}
          {summary.memberships.length > 1 && (
            <select value={summary.selected_tenant_id ?? ''} onChange={(e) => void selectTenant(e.target.value)}>
              <option value="" disabled>
                Switch tenant…
              </option>
              {summary.memberships.map((m) => (
                <option key={m.tenant.id} value={m.tenant.id}>
                  {m.tenant.configuration.name} ({m.tenant.external_id})
                </option>
              ))}
            </select>
          )}
        </div>
        <span className="muted">{summary.user.email}</span>
      </header>

      {!selected ? (
        <main>
          <TenantPicker memberships={summary.memberships} onSelect={selectTenant} />
          <Account email={summary.user.email} onSignedOut={signedOut} />
        </main>
      ) : (
        <>
          <nav className="tabs">
            {(
              [
                ['configuration', 'Configuration'],
                ['bank', 'Bank settings'],
                ['keys', 'API keys'],
                ...(canManageChildren ? [['subtenants', 'Subtenants']] : []),
                ['history', 'History'],
                ['account', 'Account'],
              ] as [Tab, string][]
            ).map(([id, label]) => (
              <button key={id} className={activeTab === id ? 'active' : ''} onClick={() => setTab(id)}>
                {label}
              </button>
            ))}
          </nav>
          {selected.blocked && (
            <p className="error">This tenant or its parent is suspended. Operations for it are refused.</p>
          )}
          {/* Keyed by tenant: switching tenants unmounts the views and clears their data and one-time secrets. */}
          <main key={selected.tenant.id}>
            {activeTab === 'configuration' && <Configuration onChanged={loadSession} />}
            {activeTab === 'bank' && <BankSettings key={selected.tenant.id} basePath="/v1/tenant/bank-connections" owner={selected.tenant} />}
            {activeTab === 'keys' && <ApiKeys canIssueManagement={canManageChildren} />}
            {activeTab === 'subtenants' && <Subtenants />}
            {activeTab === 'history' && <History path="/v1/tenant/audit-events" />}
            {activeTab === 'account' && <Account email={summary.user.email} onSignedOut={signedOut} />}
          </main>
        </>
      )}
    </div>
  )
}
