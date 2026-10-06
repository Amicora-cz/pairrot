// Thin client for the Pairrot API. The session lives in an HttpOnly cookie that scripts cannot
// read; the antiforgery token and issued key secrets are kept only in memory, never in storage.

export type TenantStatus = 'active' | 'suspended' | 'deleted'

export interface Tenant {
  id: string
  parent_tenant_id: string | null
  external_id: string
  status: TenantStatus
  configuration: { name: string; contact_email: string | null }
  capabilities: { can_create_subtenants: boolean; can_create_campaigns: boolean }
  created_at: string
  updated_at: string
  deleted_at: string | null
}

export interface Membership {
  role: string
  blocked: boolean
  tenant: Tenant
}

export interface SessionSummary {
  user: { id: string; email: string }
  session: { created_at: string; last_activity_at: string; idle_expires_at: string; absolute_expires_at: string }
  selected_tenant_id: string | null
  memberships: Membership[]
}

export interface ApiKey {
  id: string
  label: string | null
  purpose: 'tenant' | 'management'
  created_at: string
  revoked_at: string | null
}

export interface IssuedApiKey extends Omit<ApiKey, 'revoked_at'> {
  secret: string
}

export interface AuditEvent {
  id: string
  tenant_id: string
  action: string
  actor: { type: 'user' | 'api_key' | 'administrator'; user_id: string | null; email: string | null; api_key_id: string | null; description: string | null }
  details: Record<string, unknown>
  created_at: string
}

/** `stub_created` means only that a local test address exists; no bank email is received. */
export interface BankConnection {
  id: string
  tenant_id: string
  label: string | null
  account_number: string
  iban: string
  currency: string
  status: 'active' | 'archived'
  email_provider: string
  address_status: 'not_created' | 'stub_created'
  inbound_address: string | null
  address_created_at: string | null
  created_at: string
  updated_at: string
  archived_at: string | null
}

export interface ListResponse<T> {
  items: T[]
  next_cursor: string | null
}

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  /** The request field a validation error belongs to, when the API names one. */
  readonly field: string | null

  constructor(status: number, code: string, message: string, field: string | null = null) {
    super(message)
    this.status = status
    this.code = code
    this.field = field
  }
}

type Listener = (error: ApiError) => void

let antiforgeryToken: string | null = null
let expectedTenantId: string | null = null
let onAuthError: Listener = () => {}

/** Called for 401 (session gone) and for lost or changed tenant access (403/409). */
export function setAuthErrorListener(listener: Listener) {
  onAuthError = listener
}

/** The tenant this page is showing; the API rejects tenant operations if the session has moved on. */
export function setExpectedTenant(tenantId: string | null) {
  expectedTenantId = tenantId
}

/** Tokens are bound to the signed-in user, so this runs on load, after login, and after logout. */
export async function refreshAntiforgery() {
  const response = await fetch('/v1/auth/antiforgery', { credentials: 'same-origin' })
  if (!response.ok) {
    throw new ApiError(response.status, 'antiforgery_unavailable', 'Could not obtain an antiforgery token.')
  }
  antiforgeryToken = ((await response.json()) as { token: string }).token
}

const tenantAccessCodes = new Set(['tenant_context_changed', 'tenant_not_selected', 'forbidden'])

export async function api<T>(
  method: 'GET' | 'POST' | 'PATCH' | 'DELETE',
  path: string,
  body?: unknown,
  options: { background?: boolean; silent?: boolean } = {},
): Promise<T> {
  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (method !== 'GET' && antiforgeryToken) headers['X-XSRF-TOKEN'] = antiforgeryToken
  if (expectedTenantId) headers['X-Pairrot-Tenant-Id'] = expectedTenantId
  if (options.background) headers['X-Pairrot-Background'] = 'true'

  const response = await fetch(path, {
    method,
    headers,
    credentials: 'same-origin',
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (response.status === 204) return undefined as T

  const payload = await response.json().catch(() => null)
  if (!response.ok) {
    const error = new ApiError(
      response.status,
      payload?.error?.code ?? 'http_error',
      payload?.error?.message ?? `Request failed with status ${response.status}.`,
      payload?.error?.field ?? null,
    )
    const tenantAccessLost = (response.status === 403 || response.status === 409) && tenantAccessCodes.has(error.code)
    if (!options.silent && (response.status === 401 || tenantAccessLost)) onAuthError(error)
    throw error
  }

  return payload as T
}
