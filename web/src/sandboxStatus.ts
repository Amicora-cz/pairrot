import { createContext, useContext } from 'react'
import { api } from './api'

/** Feature 21: `GET /v1/sandbox`. Present only while the local API runs in sandbox mode; a 404 means off. */
export interface SandboxStatus {
  enabled: boolean
  profiles: string[]
}

/** `null` while sandbox mode is off or unknown: nothing sandbox-related is rendered. */
export const SandboxContext = createContext<SandboxStatus | null>(null)

export function useSandbox() {
  const status = useContext(SandboxContext)
  return status?.enabled ? status : null
}

/** Loads the sandbox status once; any failure, a 404 included, means no sandbox. */
export async function loadSandbox(): Promise<SandboxStatus | null> {
  try {
    const status = await api<SandboxStatus>('GET', '/v1/sandbox', undefined, { silent: true })
    return status?.enabled ? status : null
  } catch {
    return null
  }
}
