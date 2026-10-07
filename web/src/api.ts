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

export interface ErrorAt {
  code: string
  message: string
  at: string | null
}

/**
 * `stub_created` means only that a local test address exists; no email is delivered to it.
 * `live_created` means a real receiving address exists, not that a bank has sent anything.
 * `pending` and `failed` describe provider setup of a reserved address, which is not shown yet.
 */
export interface BankConnection {
  id: string
  tenant_id: string
  label: string | null
  account_number: string
  iban: string
  currency: string
  status: 'active' | 'archived'
  /** Provider kind of the current address, or of the server's default instance before one is assigned. */
  email_provider: string
  provider_instance_id: string | null
  address_status: 'not_created' | 'pending' | 'failed' | 'stub_created' | 'live_created'
  inbound_address: string | null
  address_created_at: string | null
  provisioning_error: string | null
  /** Separate facts; none proves that a bank sent the email or that a payment arrived. */
  reception: {
    last_message_received_at: string | null
    last_content_stored_at: string | null
    last_content_error: ErrorAt | null
  }
  created_at: string
  updated_at: string
  archived_at: string | null
  /** Address assignment only: a test address was retired for a new live one. */
  address_replaced?: boolean
}

export type InboundHoldReason = 'address_retired' | 'bank_connection_archived' | 'tenant_not_active' | 'recipient_mismatch'

/** Normalized results computed by the receiving system; `unknown` when there is no evidence. */
export interface SenderAuthentication {
  spf: string
  dkim: string
  dmarc: string
  origin: string | null
}

/** A received email. `available` means its source is stored, not that it was parsed or matched. */
export interface InboundMessage {
  id: string
  tenant_id: string
  bank_connection_id: string
  provider_instance_id: string
  transport_kind: string
  source_kind: 'received' | 'fixture'
  environment: 'test' | 'live'
  routing_state: 'routed'
  hold_reason: InboundHoldReason | null
  content_mode: 'deferred' | 'inline'
  content_state: 'pending' | 'available' | 'failed'
  header_from: string | null
  envelope_sender: string | null
  subject: string | null
  envelope_recipients: string[]
  recipient_evidence: string
  received_at: string
  content_stored_at: string | null
  content_sha256: string | null
  sender_authentication: SenderAuthentication
  content_error: ErrorAt | null
  source_available: boolean
  created_at: string
  updated_at: string
  /** Detail only; none for inline content. */
  retrievals?: {
    id: string
    generation: number
    state: 'queued' | 'running' | 'retry_wait' | 'succeeded' | 'failed'
    attempt_count: number
    next_attempt_at: string | null
    last_error: ErrorAt | null
    requested_by: string
    created_at: string
    completed_at: string | null
  }[]
}

/** Stored common source. `html` is the email's HTML as text: display it escaped, never render it. */
export interface InboundMessageSource {
  message_id: string
  source_kind: 'received' | 'fixture'
  environment: 'test' | 'live'
  header_from: string | null
  subject: string | null
  envelope_recipients: string[]
  content_recipients: string[] | null
  received_at: string
  content_stored_at: string
  content_sha256: string
  text: string | null
  html: string | null
  headers: Record<string, unknown> | null
  sender_authentication: SenderAuthentication
  authentication_raw: unknown
  attachments: unknown[] | null
}

/** Payment data for a QR code. Generating one stores nothing and does not track the payment. */
export interface PaymentQr {
  bank_connection_id: string
  tenant_id: string
  account_number: string
  iban: string
  /** Minor units: 45000 is CZK 450.00. */
  amount: number
  currency: string
  variable_symbol: string | null
  message: string | null
  format: 'SPAYD'
  payload: string
}

/**
 * An expected payment. Payment fields are frozen at creation; only `description` and `due_at`
 * can change. `paid_amount` stays 0 until payments are matched (a later feature).
 */
export interface PaymentRequest {
  id: string
  tenant_id: string
  bank_connection_id: string
  external_id: string
  account_number: string
  iban: string
  /** Minor units: 45000 is CZK 450.00. */
  amount: number
  currency: string
  variable_symbol: string
  message: string | null
  description: string | null
  due_at: string | null
  status: 'open' | 'cancelled'
  is_overdue: boolean
  paid_amount: number
  /** `null` for a cancelled request: nothing more is being asked for. */
  remaining_amount: number | null
  cancellation_reason: string | null
  created_at: string
  updated_at: string
  cancelled_at: string | null
  /** Sent back in `If-Match` when editing or cancelling. */
  version: number
  /** Creation only. */
  replayed?: boolean
  /** Creation only, while the request is payable. */
  payment_instructions?: PaymentInstructions
}

/** The instructions saved with a payment request; the payload is never regenerated. */
export interface PaymentInstructions {
  payment_request_id: string
  bank_connection_id: string
  tenant_id: string
  account_number: string
  iban: string
  amount: number
  currency: string
  variable_symbol: string
  message: string | null
  format: 'SPAYD'
  generator_version: string
  payload: string
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

type Method = 'GET' | 'POST' | 'PATCH' | 'DELETE'

interface SendOptions {
  background?: boolean
  silent?: boolean
  /** The record version this change is based on; a newer one makes the API answer 412. */
  ifMatch?: number
}

async function send(method: Method, path: string, body: unknown, options: SendOptions) {
  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (method !== 'GET' && antiforgeryToken) headers['X-XSRF-TOKEN'] = antiforgeryToken
  if (expectedTenantId) headers['X-Pairrot-Tenant-Id'] = expectedTenantId
  if (options.background) headers['X-Pairrot-Background'] = 'true'
  if (options.ifMatch !== undefined) headers['If-Match'] = `"${options.ifMatch}"`

  const response = await fetch(path, {
    method,
    headers,
    credentials: 'same-origin',
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (!response.ok) {
    const payload = await response.json().catch(() => null)
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

  return response
}

export async function api<T>(
  method: Method,
  path: string,
  body?: unknown,
  options: SendOptions = {},
): Promise<T> {
  const response = await send(method, path, body, options)
  if (response.status === 204) return undefined as T
  return (await response.json().catch(() => null)) as T
}

/** Returns the PNG a path produces: POSTed with a JSON body, or a GET without one. Anything that is not a PNG is an error. */
export async function apiPng(path: string, body?: unknown): Promise<Blob> {
  const response = await send(body === undefined ? 'GET' : 'POST', path, body, {})
  if (response.headers.get('Content-Type')?.split(';')[0].trim() !== 'image/png') {
    throw new ApiError(response.status, 'unexpected_response', 'The server did not return a PNG image.')
  }
  return response.blob()
}
