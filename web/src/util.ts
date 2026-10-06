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
