import { useState } from 'react'
import { api, ApiError, type PaymentRequest } from './api'
import { ErrorText } from './components'
import { formatMinorUnits, toMinorUnits, useAction } from './util'

interface Accepted {
  inbound_message_id: string
  bank_connection_id: string
  profile: string
  status: string
  payment_request_id?: string
}

function AcceptedNotice({ result }: { result: Accepted }) {
  return (
    <p className="notice">
      Accepted as inbound message <code>{result.inbound_message_id}</code> (profile {result.profile}). It is processed in the background;
      find it under Inbound emails and Bank transactions.
    </p>
  )
}

const fieldErrorOf = (error: unknown, field: string) =>
  error instanceof ApiError && error.field === field ? <span className="field-error">{error.message}</span> : null

/** Simulates the payer paying one payment request, prefilled with the remainder and the request's variable symbol. */
export function SimulatePayment({ path, request: r, onDone }: { path: string; request: PaymentRequest; onDone?: () => void }) {
  const remainder = Math.max(r.remaining_amount ?? r.amount, 1)
  const [form, setForm] = useState({ amount: (remainder / 100).toFixed(2), variable_symbol: r.variable_symbol, payer_name: '', booked_at: '' })
  const [result, setResult] = useState<Accepted | null>(null)
  const { busy, error, run, setError } = useAction()
  const generalError = error instanceof ApiError && error.field ? null : error
  const edit = (change: Partial<typeof form>) => {
    setForm({ ...form, ...change })
    setError(null)
    setResult(null)
  }

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault()
        const amount = toMinorUnits(form.amount)
        if (amount === null) {
          setError(new ApiError(400, 'invalid_amount', 'Enter an amount in CZK with at most two decimal places, e.g. 450 or 450.50.', 'amount'))
          return
        }
        // Only what differs from the API's defaults is sent; validation happens in the API.
        const body: Record<string, string | number> = { amount }
        if (form.variable_symbol.trim()) body.variable_symbol = form.variable_symbol.trim()
        if (form.payer_name.trim()) body.payer_name = form.payer_name.trim()
        if (form.booked_at) body.booked_at = new Date(form.booked_at).toISOString()
        void run(async () => {
          setResult(await api<Accepted>('POST', `${path}/sandbox/pay`, body))
          onDone?.()
        })
      }}
    >
      <h3>Simulate payment</h3>
      <p className="muted">
        Sandbox only: a bank notification is rendered and processed like a real one, as test data. Unpaid remainder:{' '}
        {formatMinorUnits(remainder, r.currency)}.
      </p>
      <label>
        Amount (CZK)
        <input inputMode="decimal" value={form.amount} onChange={(e) => edit({ amount: e.target.value })} required />
        {fieldErrorOf(error, 'amount')}
      </label>
      <label>
        Variable symbol
        <input inputMode="numeric" maxLength={10} value={form.variable_symbol} onChange={(e) => edit({ variable_symbol: e.target.value })} />
        {fieldErrorOf(error, 'variable_symbol')}
      </label>
      <label>
        Payer name (optional)
        <input value={form.payer_name} onChange={(e) => edit({ payer_name: e.target.value })} />
        {fieldErrorOf(error, 'payer_name')}
      </label>
      <label>
        Booked (optional, your local time)
        <input type="datetime-local" value={form.booked_at} onChange={(e) => edit({ booked_at: e.target.value })} />
        {fieldErrorOf(error, 'booked_at')}
      </label>
      <ErrorText error={generalError} />
      {result && <AcceptedNotice result={result} />}
      <button type="submit" disabled={busy}>
        {busy ? 'Sending…' : 'Simulate payment'}
      </button>
    </form>
  )
}

/** Simulates any incoming (or outgoing) movement on one bank connection: all fields of the sandbox API. */
export function SimulateIncomingPayment({ path }: { path: string }) {
  const empty = {
    amount: '',
    variable_symbol: '',
    message: '',
    payer_name: '',
    payer_account: '',
    booked_at: '',
    bank_reference: '',
    direction: 'incoming',
  }
  const [form, setForm] = useState(empty)
  const [result, setResult] = useState<Accepted | null>(null)
  const { busy, error, run, setError } = useAction()
  const generalError = error instanceof ApiError && error.field ? null : error
  const edit = (change: Partial<typeof form>) => {
    setForm({ ...form, ...change })
    setError(null)
    setResult(null)
  }

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault()
        const amount = toMinorUnits(form.amount)
        if (amount === null) {
          setError(new ApiError(400, 'invalid_amount', 'Enter an amount in CZK with at most two decimal places, e.g. 450 or 450.50.', 'amount'))
          return
        }
        const body: Record<string, string | number> = { amount, direction: form.direction }
        for (const key of ['variable_symbol', 'message', 'payer_name', 'payer_account', 'bank_reference'] as const) {
          if (form[key].trim()) body[key] = form[key].trim()
        }
        if (form.booked_at) body.booked_at = new Date(form.booked_at).toISOString()
        void run(async () => setResult(await api<Accepted>('POST', `${path}/sandbox/incoming-payments`, body)))
      }}
    >
      <h3>Simulate incoming payment</h3>
      <p className="muted">
        Sandbox only: a bank notification is rendered and processed like a real one, as test data. Sending it again simulates the bank
        sending it twice.
      </p>
      <label>
        Amount (CZK)
        <input inputMode="decimal" placeholder="450.00" value={form.amount} onChange={(e) => edit({ amount: e.target.value })} required />
        {fieldErrorOf(error, 'amount')}
      </label>
      <label>
        Variable symbol (optional)
        <input inputMode="numeric" maxLength={10} value={form.variable_symbol} onChange={(e) => edit({ variable_symbol: e.target.value })} />
        {fieldErrorOf(error, 'variable_symbol')}
      </label>
      <label>
        Message (optional)
        <input maxLength={140} value={form.message} onChange={(e) => edit({ message: e.target.value })} />
        {fieldErrorOf(error, 'message')}
      </label>
      <label>
        Payer name (optional)
        <input placeholder="Sandbox Plátce" value={form.payer_name} onChange={(e) => edit({ payer_name: e.target.value })} />
        {fieldErrorOf(error, 'payer_name')}
      </label>
      <label>
        Payer account (optional)
        <input placeholder="19-2000145399/0100" value={form.payer_account} onChange={(e) => edit({ payer_account: e.target.value })} />
        {fieldErrorOf(error, 'payer_account')}
      </label>
      <label>
        Booked (optional, your local time)
        <input type="datetime-local" value={form.booked_at} onChange={(e) => edit({ booked_at: e.target.value })} />
        {fieldErrorOf(error, 'booked_at')}
      </label>
      <label>
        Bank reference (optional)
        <input value={form.bank_reference} onChange={(e) => edit({ bank_reference: e.target.value })} />
        <span className="muted">Only the sandbox bank profile has one; a real profile such as Česká spořitelna refuses it.</span>
        {fieldErrorOf(error, 'bank_reference')}
      </label>
      <label>
        Direction
        <select value={form.direction} onChange={(e) => edit({ direction: e.target.value })}>
          <option value="incoming">incoming</option>
          <option value="outgoing">outgoing (debit, not a payment)</option>
        </select>
        {fieldErrorOf(error, 'direction')}
      </label>
      <ErrorText error={generalError} />
      {result && <AcceptedNotice result={result} />}
      <button type="submit" disabled={busy}>
        {busy ? 'Sending…' : 'Simulate incoming payment'}
      </button>
    </form>
  )
}
