/** The slice of Google Identity Services (https://accounts.google.com/gsi/client) that this app uses. */
interface GoogleIdentity {
  accounts: {
    id: {
      initialize: (config: { client_id: string; callback: (response: { credential?: string }) => void }) => void
      renderButton: (parent: HTMLElement, options: Record<string, unknown>) => void
    }
  }
}

declare global {
  interface Window {
    google?: GoogleIdentity
  }
}

const SCRIPT_URL = 'https://accounts.google.com/gsi/client'
let scriptPromise: Promise<void> | null = null

/** Loads Google's sign-in script once. Resolves immediately when it is already on the page. */
export function loadGoogleScript(): Promise<void> {
  if (window.google?.accounts?.id) return Promise.resolve()
  scriptPromise ??= new Promise<void>((resolve, reject) => {
    const script = document.createElement('script')
    script.src = SCRIPT_URL
    script.async = true
    script.onload = () => resolve()
    script.onerror = () => {
      script.remove()
      scriptPromise = null // allow a retry (for example after an ad blocker is switched off)
      reject(new Error('The Google sign-in script could not be loaded.'))
    }
    document.head.appendChild(script)
  })
  return scriptPromise
}
