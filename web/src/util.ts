import { useCallback, useState } from 'react'
import { ApiError } from './api'

export function formatDate(value: string | null) {
  return value ? new Date(value).toLocaleString() : '—'
}

export function errorMessage(error: unknown) {
  if (error instanceof ApiError) return `${error.message} (${error.code})`
  return error instanceof Error ? error.message : String(error)
}

/** Runs an async action with a busy flag and captured error. */
export function useAction() {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const run = useCallback(async (action: () => Promise<void>) => {
    setBusy(true)
    setError(null)
    try {
      await action()
    } catch (e) {
      setError(e)
    } finally {
      setBusy(false)
    }
  }, [])
  return { busy, error, run, setError }
}

/**
 * Converts a typed CZK amount ("450", "450.5", "1 250,00") to minor units with integer arithmetic
 * only. Returns null when the text is not a plain amount with at most two decimal places.
 */
export function toMinorUnits(text: string): number | null {
  const match = /^(\d{1,7})(?:[.,](\d{1,2}))?$/.exec(text.replace(/\s/g, ''))
  if (!match) return null
  return Number(match[1]) * 100 + Number((match[2] ?? '').padEnd(2, '0'))
}

export function formatMinorUnits(minor: number, currency: string) {
  return `${currency} ${Math.trunc(minor / 100).toLocaleString('en-US')}.${String(minor % 100).padStart(2, '0')}`
}
