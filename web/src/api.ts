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
  /** Feature 14: when the erasure finished; the name and external_id are placeholders since. */
  erased_at: string | null
  /** Returned for a single subtenant once its erasure was requested. */
  erasure?: Erasure
}

/** Feature 14: the background erasure of a deleted tenant's personal data. */
export interface Erasure {
  id: string
  state: 'queued' | 'running' | 'done'
  requested_at: string
  completed_at: string | null
  /** The technical actor, e.g. `api_key:…`; never a name. */
  requested_by: string
  job_id: string
  /** Records changed per record type so far. */
  counts: Record<string, number>
  /** What Pairrot cannot undo, e.g. `sent_webhook_copies_not_recalled`. */
  limitations: string[]
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
  /** Feature 13: the physical account; every registration of the same account shares it. */
  bank_account_id: string
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
  /** Why the connection is archived; `null` while it is active. */
  archived_reason: 'user' | 'tenant_deleted' | 'operator' | null
  /** Feature 14: erased with its tenant; the label is gone and the account is masked. */
  erased_at: string | null
  /** Address assignment only: a test address was retired for a new live one. */
  address_replaced?: boolean
}

export type InboundHoldReason =
  | 'address_retired'
  | 'bank_connection_archived'
  | 'tenant_not_active'
  | 'recipient_mismatch'
  | 'address_paused'

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
  /** Feature 8: who the delivery metadata says sent it; limits collection, not a trust decision. */
  sender_class: 'bank' | 'forwarding_confirmation' | 'tenant_forward' | 'foreign' | null
  content_mode: 'deferred' | 'inline'
  /** `expired` and `erased` (Feature 14) are terminal: the content was purged and is never retrieved again. */
  content_state: 'pending' | 'available' | 'failed' | 'expired' | 'erased'
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
  /** Latest completed parse run (Feature 007); separate from the content state. */
  latest_parse_run_id: string | null
  latest_parse_outcome: ParseOutcome | null
  parsed_at: string | null
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

export type ParseOutcome = 'parsed' | 'duplicate' | 'not_payment' | 'unsupported' | 'review_required' | 'failed'

export interface ParseReason {
  code: string
  message: string
}

/** One parse attempt. `candidate` is what was extracted: under review it is a proposal, not a recorded receipt. */
export interface ParseRun {
  id: string
  inbound_message_id: string
  generation: number
  source_sha256: string
  bank_profile: string | null
  parser_version: string | null
  trust_decision: 'trusted' | 'untrusted' | null
  trust: {
    decision: string
    reasons: ParseReason[]
    evidence: {
      from_address: string | null
      from_domain: string | null
      spf: string
      dkim: string
      dmarc: string
      authentication_origin: string | null
      source_kind: string
      environment: string
    }
  } | null
  outcome: ParseOutcome
  reason_code: string
  reason_message: string
  reasons: ParseReason[]
  template_id: string | null
  source_part: 'text' | 'html' | null
  candidate: {
    amount: number | null
    currency: string | null
    variable_symbol: string | null
    bank_reference: string | null
    destination: string
    sender_account: string | null
    sender_name: string | null
    message: string | null
    booked_on: string | null
    occurred_at: string | null
    raw: Record<string, string | null>
  } | null
  transaction_id: string | null
  requested_by: string
  started_at: string
  completed_at: string
}

export interface ParseHistory {
  inbound_message_id: string
  content_state: string
  latest_outcome: ParseOutcome | null
  runs: ParseRun[]
  jobs: NonNullable<InboundMessage['retrievals']>
}

export type IngestionStatus = 'accepted' | 'review_required' | 'disputed'

export type HandlingState = 'unallocated' | 'allocated' | 'ignored' | 'rejected' | 'duplicate'

/** Feature 9: matching state of a transaction. `result` is the latest automatic attempt; a suggestion is not a reservation. */
export interface TransactionMatching {
  result: string | null
  payment_request_id: string | null
  checked_at: string | null
  auto_matching_blocked: boolean
  auto_matching_block_reason: string | null
  /** A person confirmed the receipt in a review; the provider's authentication results are unchanged. */
  manually_confirmed: boolean
  handling_reason: string | null
  handled_at: string | null
  handled_by: string | null
  canonical_transaction_id: string | null
  allocation: Allocation | null
  open_review_case_ids: string[]
  allocatable: boolean
  manual_block: string | null
  automatic_block: string | null
}

/** The recorded link between a transaction and the request it pays, always for the full amount. Never edited, only revoked. */
export interface Allocation {
  id: string
  transaction_id: string
  payment_request_id: string
  /** The transaction's connection. */
  bank_connection_id: string
  /** The request's connection: another registration of the same account is possible (Feature 13). */
  payment_request_bank_connection_id: string
  bank_account_id: string
  amount: number
  currency: string
  method: 'automatic' | 'manual'
  reason: string | null
  evidence: 'live' | 'test' | 'fixture'
  state: 'active' | 'revoked'
  created_by: string
  created_at: string
  revoked_by: string | null
  revoked_at: string | null
  revocation_code: string | null
  revocation_reason: string | null
  version: number
}

export interface PaymentSummary {
  status: PaymentRequestStatus
  paid_amount: number
  remaining_amount: number | null
  overpaid_amount: number
}

export interface AllocationOutcome {
  allocation: Allocation
  transaction: IncomingTransaction
  payment_request: PaymentRequest
}

export interface MatchingCandidates {
  transaction: IncomingTransaction
  candidates: { payment_request: PaymentRequest; reasons: string[]; proposed: PaymentSummary | null; block: string | null }[]
}

export interface PaymentChange {
  event_id: string
  payment_request_id: string
  sequence: number
  external_id: string
  expected_amount: number
  currency: string
  before: PaymentSummary
  after: PaymentSummary
  cause: 'automatic_allocation' | 'manual_allocation' | 'allocation_revoked' | 'transaction_review_required' | 'cancelled'
  allocation_id: string | null
  transaction_id: string | null
  evidence: string | null
  actor: string
  created_at: string
}

export type ReviewIssue = 'unverified_candidate' | 'possible_duplicate' | 'disputed' | 'account_movement_conflict'
export type ReviewDecision = 'confirm_payment' | 'confirm_distinct' | 'mark_duplicate' | 'keep_existing' | 'reject'

export interface ReviewCase {
  id: string
  tenant_id: string
  bank_connection_id: string
  bank_account_id: string
  issue: ReviewIssue
  state: 'open' | 'resolved'
  inbound_message_id: string | null
  reason_code: string
  reason_message: string
  resolution: string | null
  created_at: string
  updated_at: string
  resolved_at: string | null
  version: number
}

export interface ReviewCaseDetail {
  case: ReviewCase
  transactions: IncomingTransaction[]
  parse_runs: ParseRun[]
  decisions: {
    id: string
    action: string
    reason: string
    actor: string
    case_version: number
    members: { transaction_id: string; version: number }[]
    canonical_transaction_id: string | null
    created_transaction_id: string | null
    created_at: string
  }[]
}

/**
 * A bank movement recorded from notifications. It counts as money only through an allocation
 * (Feature 9); records under review or with test evidence never pay a request automatically.
 */
export interface IncomingTransaction {
  id: string
  tenant_id: string
  bank_connection_id: string
  bank_account_id: string
  amount: number
  currency: string
  variable_symbol: string | null
  bank_transaction_id: string | null
  sender_account: string | null
  sender_name: string | null
  message: string | null
  booked_on: string
  occurred_at: string | null
  received_at: string
  bank_profile: string
  parser_version: string
  ingestion_status: IngestionStatus
  ingestion_reasons: { code: string; message: string; parse_run_id: string | null; at: string }[]
  evidence: 'live' | 'test' | 'fixture'
  origin_path: 'direct' | 'automatic_forward' | 'manual_forward'
  /** Feature 9: what has been decided about this money. */
  handling_state: HandlingState
  /** Sent back as `expected_transaction_version` / `expected_version` in manual writes. */
  version: number
  matching: TransactionMatching | null
  created_at: string
  updated_at: string
  /** Feature 14: the sender fields were purged (retention or erasure); amounts and symbols remain. */
  payer_data_purged_at: string | null
  erased_at: string | null
  /** Detail only. */
  sources?: { inbound_message_id: string; parse_run_id: string; linked_at: string }[]
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

export type PaymentRequestStatus = 'open' | 'partially_paid' | 'paid' | 'overpaid' | 'cancelled'

/**
 * An expected payment. Payment fields are frozen at creation; only `description` and `due_at`
 * can change while something is left to pay. `paid_amount` is the sum of active allocations.
 */
export interface PaymentRequest {
  id: string
  tenant_id: string
  bank_connection_id: string
  bank_account_id: string
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
  status: PaymentRequestStatus
  is_overdue: boolean
  paid_amount: number
  /** `null` for a cancelled request: nothing more is being asked for. */
  remaining_amount: number | null
  overpaid_amount: number
  /** Detail only: open review cases about money allocated to this request. */
  open_review_case_ids?: string[]
  cancellation_reason: string | null
  created_at: string
  updated_at: string
  cancelled_at: string | null
  /** Sent back in `If-Match` when editing or cancelling. */
  version: number
  /** Feature 14: erased with its tenant; `external_id` is a tombstone and the free text is gone. */
  erased_at: string | null
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
  /** Required by manual matching and review commands; reuse it when retrying the same submission. */
  idempotencyKey?: string
}

async function send(method: Method, path: string, body: unknown, options: SendOptions) {
  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (method !== 'GET' && antiforgeryToken) headers['X-XSRF-TOKEN'] = antiforgeryToken
  if (expectedTenantId) headers['X-Pairrot-Tenant-Id'] = expectedTenantId
  if (options.background) headers['X-Pairrot-Background'] = 'true'
  if (options.ifMatch !== undefined) headers['If-Match'] = `"${options.ifMatch}"`
  if (options.idempotencyKey) headers['Idempotency-Key'] = options.idempotencyKey

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

/** Feature 8: a mailbox that forwards bank notifications, entered by the tenant and active at once. */
export interface ForwardingSource {
  id: string
  /** `null` once erased with the tenant. */
  address: string | null
  state: 'active' | 'removed'
  created_at: string
  erased_at: string | null
}

export type SetupStatus =
  | 'address_not_created'
  | 'waiting_for_first_message'
  | 'confirmation_received'
  | 'bank_message_trusted'
  | 'bank_message_untrusted'
  | 'paused_foreign_mail'

/** Whether forwarding works, derived from received mail. Foreign mail appears only as counts and domains. */
export interface ReceptionStatus {
  setup_status: SetupStatus
  inbound_address: string | null
  address_processing: 'active' | 'paused_foreign_mail' | null
  paused_at: string | null
  operator_resume_required: boolean
  foreign_messages_in_window: number
  recent_foreign_domains: string[]
  /** A mail provider's forwarding confirmation; `excerpt` is plain text, links are never opened. */
  confirmation: { message_id: string; received_at: string; sender_domain: string | null; code: string | null; excerpt: string } | null
  latest_bank_message: {
    message_id: string
    received_at: string
    parse_outcome: string | null
    trust_decision: string | null
    origin_path: string | null
    forwarding_source: string | null
  } | null
  forwarding_sources: ForwardingSource[]
}

/** Features 010 and 011: the tenant's outgoing payment-status webhook. Secrets appear only in a creation or rotation response. */
export type WebhookScope = 'own' | 'own_and_direct_children'
export type WebhookHealth = 'paused' | 'configured' | 'test_accepted' | 'delivering' | 'failing'
export type WebhookDeliveryState = 'pending' | 'running' | 'retry_wait' | 'delivered' | 'failed' | 'held' | 'abandoned'

export interface WebhookEndpoint {
  id: string
  tenant_id: string
  /** `null` once the endpoint was erased with its tenant. */
  url: string | null
  scope: WebhookScope
  state: 'active' | 'paused'
  environment: 'live' | 'test'
  destination_revision: number
  signing_keys: { key_id: string; state: 'current' | 'retiring' | 'retired'; created_at: string; retires_at: string | null; retired_at: string | null }[]
  deliveries: { pending: number; retry_wait: number; running: number; failed: number; held: number; delivered: number; abandoned: number }
  /** Evidence, not configuration: a saved URL alone is only `configured`. */
  health: { status: WebhookHealth; last_test_accepted_at: string | null; last_success_at: string | null; current_failures: number }
  created_at: string
  updated_at: string
  version: number
}

/** Returned once by creation and rotation; a retried command returns the endpoint without it. */
export interface SignedWebhookEndpoint {
  endpoint: WebhookEndpoint
  signing_secret?: { key_id: string; secret: string }
}

export interface WebhookEvent {
  id: string
  type: string
  tenant_id: string
  payment_request_id: string | null
  sequence: number | null
  environment: 'live' | 'test'
  synthetic: boolean
  created_at: string
  /** The exact JSON sent; `null` once erased with its owner (Feature 14), never rebuilt. */
  body: unknown
  body_sha256: string
  payload_erased_at: string | null
}

export interface WebhookDelivery {
  id: string
  event_id: string
  endpoint_id: string
  owner_tenant_id: string
  destination_revision: number
  state: WebhookDeliveryState
  hold_reason: string | null
  generation: number
  attempt_in_generation: number
  max_attempts: number
  total_attempts: number
  next_attempt_at: string | null
  last_outcome: string | null
  last_error_code: string | null
  last_error_message: string | null
  last_http_status: number | null
  abandon_reason: string | null
  delivered_at: string | null
  created_at: string
  updated_at: string
  version: number
}

export interface WebhookAttempt {
  id: string
  generation: number
  attempt_number: number
  destination_revision: number
  /** `null` once the endpoint was erased with its tenant. */
  url: string | null
  key_ids: string
  outcome: 'in_progress' | 'delivered' | 'retryable_failure' | 'permanent_failure' | 'outcome_unknown'
  http_status: number | null
  error_code: string | null
  response_excerpt: string | null
  started_at: string
  completed_at: string | null
  duration_ms: number | null
}

export interface WebhookDeliveryDetail {
  delivery: WebhookDelivery
  event: WebhookEvent
  attempts: WebhookAttempt[]
  current_url: string | null
}
