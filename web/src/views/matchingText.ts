import type { IncomingTransaction } from '../api'
import { words } from '../util'

/** Why a transaction was or was not matched automatically, in words. */
export const MATCH_RESULTS: Record<string, string> = {
  matched: 'matched automatically',
  missing_variable_symbol: 'no variable symbol in the payment',
  request_not_found: 'no payment request with this variable symbol',
  amount_mismatch: 'the amount differs from the expected amount',
  currency_mismatch: 'the currency differs',
  request_cancelled: 'the payment request is cancelled',
  request_already_paid: 'the request already has a payment; a top-up is allocated by hand',
  multiple_candidates: 'more than one request fits',
  review_required: 'the transaction needs review first',
  manual_allocation_only: 'confirmed in a review; allocated by hand only',
  auto_matching_blocked: 'automatic matching is switched off for it',
  not_live_evidence: 'test or fixture data never pays a request',
  tenant_not_active: 'the tenant is not active',
  bank_connection_archived: 'the bank account is archived',
  already_allocated: 'already allocated',
  ignored: 'ignored',
  rejected: 'rejected in a review',
  duplicate: 'a copy of another transaction',
  account_movement_conflict: 'an earlier registration of this bank account may have recorded it; an operator decides',
}

export const ORIGIN_LABELS: Record<IncomingTransaction['origin_path'], string> = {
  direct: 'directly from the bank',
  automatic_forward: 'automatically forwarded from a registered mailbox',
  manual_forward: 'forwarded by hand from a registered mailbox',
}

export function explain(code: string | null | undefined) {
  return code ? (MATCH_RESULTS[code] ?? words(code)) : '—'
}
