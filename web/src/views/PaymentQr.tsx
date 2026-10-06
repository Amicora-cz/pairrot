import { useEffect, useRef, useState } from 'react'
import { api, apiPng, ApiError, type BankConnection, type PaymentQr, type Tenant } from '../api'
import { ErrorText } from '../components'
import { formatMinorUnits, toMinorUnits, useAction } from '../util'

interface Preview {
  data: PaymentQr
  imageUrl: string
}

/** Generates a payment QR for one active connection. The preview always matches the submitted values. */
export function PaymentQrPanel({ basePath, connection, owner }: { basePath: string; connection: BankConnection; owner: Tenant }) {
  const [form, setForm] = useState({ amount: '', variable_symbol: '', message: '' })
  const [preview, setPreview] = useState<Preview | null>(null)
  const [copied, setCopied] = useState(false)
  const { busy, error, run, setError } = useAction()
  // Bumped on every edit, so a response for values the user has since changed is discarded.
  const generation = useRef(0)
  const path = `${basePath}/${connection.id}/payment-qr`

  // Release the image when it is replaced or the panel closes.
  useEffect(() => () => {
    if (preview) URL.revokeObjectURL(preview.imageUrl)
  }, [preview])

  const edit = (change: Partial<typeof form>) => {
    generation.current++
    setForm({ ...form, ...change })
    setPreview(null)
    setError(null)
    setCopied(false)
  }

  const fieldError = (field: string) =>
    error instanceof ApiError && error.field === field ? <span className="field-error">{error.message}</span> : null
  const generalError = error instanceof ApiError && error.field ? null : error

  return (
    <div className="payment-qr">
      <h3>Generate payment QR</h3>
      <p className="muted">
        The code contains payment instructions only. Pairrot does not yet track whether the payment arrives; scanning or
        paying it is not a confirmation in Pairrot. A code for a real account may prefill a real payment in a banking app.
      </p>
      <dl>
        <dt>Recipient</dt>
        <dd>
          {owner.configuration.name} <code>{owner.external_id}</code>
        </dd>
        <dt>Account</dt>
        <dd>
          <code>{connection.account_number}</code> (<code>{connection.iban}</code>)
        </dd>
        <dt>Currency</dt>
        <dd>CZK</dd>
      </dl>
      <form
        onSubmit={(e) => {
          e.preventDefault()
          const amount = toMinorUnits(form.amount)
          if (amount === null) {
            setPreview(null)
            setError(new ApiError(400, 'invalid_amount', 'Enter an amount in CZK with at most two decimal places, e.g. 450 or 450.50.', 'amount'))
            return
          }
          const body: Record<string, string | number> = { amount, currency: 'CZK' }
          if (form.variable_symbol.trim()) body.variable_symbol = form.variable_symbol.trim()
          if (form.message.trim()) body.message = form.message
          setPreview(null)
          const submitted = ++generation.current
          void run(async () => {
            const data = await api<PaymentQr>('POST', path, body)
            const image = await apiPng(`${path}.png`, body)
            if (submitted === generation.current) setPreview({ data, imageUrl: URL.createObjectURL(image) })
          })
        }}
      >
        <label>
          Amount (CZK)
          <input inputMode="decimal" placeholder="450.00" value={form.amount} onChange={(e) => edit({ amount: e.target.value })} />
          {fieldError('amount')}
        </label>
        <label>
          Variable symbol (optional)
          <input
            inputMode="numeric"
            maxLength={10}
            placeholder="0000123456"
            value={form.variable_symbol}
            onChange={(e) => edit({ variable_symbol: e.target.value })}
          />
          {fieldError('variable_symbol')}
        </label>
        <label>
          Message for the recipient (optional)
          <input placeholder="Yoga October" value={form.message} onChange={(e) => edit({ message: e.target.value })} />
          <span className="muted">Up to 60 characters; Czech letters are fine, * and % are not allowed.</span>
          {fieldError('message')}
        </label>
        <ErrorText error={generalError} />
        <button type="submit" disabled={busy}>
          {busy ? 'Generating…' : 'Generate QR'}
        </button>
      </form>

      {preview && (
        <div className="qr-preview">
          <img src={preview.imageUrl} alt="Payment QR code" />
          <dl>
            <dt>Account</dt>
            <dd>
              <code>{preview.data.account_number}</code>
            </dd>
            <dt>IBAN</dt>
            <dd>
              <code>{preview.data.iban}</code>
            </dd>
            <dt>Amount</dt>
            <dd>{formatMinorUnits(preview.data.amount, preview.data.currency)}</dd>
            <dt>Variable symbol</dt>
            <dd>{preview.data.variable_symbol ?? '—'}</dd>
            <dt>Message</dt>
            <dd>{preview.data.message ?? '—'}</dd>
            <dt>Payload ({preview.data.format})</dt>
            <dd>
              <code className="payload">{preview.data.payload}</code>
            </dd>
          </dl>
          <div className="row">
            <button
              className="secondary"
              onClick={async () => {
                await navigator.clipboard.writeText(preview.data.payload)
                setCopied(true)
              }}
            >
              {copied ? 'Copied' : 'Copy payment data'}
            </button>
            <a className="button" href={preview.imageUrl} download={`payment-qr${preview.data.variable_symbol ? `-${preview.data.variable_symbol}` : ''}.png`}>
              Download PNG
            </a>
          </div>
        </div>
      )}
    </div>
  )
}
