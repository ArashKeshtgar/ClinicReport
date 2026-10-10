import { useEffect, useState, type FormEvent } from 'react'
import QRCode from 'qrcode'
import { api, ApiError, type Me } from '../api'

/** Scan a QR code with an authenticator app, then confirm with one code. */
export function TwoFactorSetup({ onEnabled }: { onEnabled: (me: Me) => void }) {
  const [setup, setSetup] = useState<{ secret: string; qr: string } | null>(null)
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.twoFactorSetup()
      .then(async s => setSetup({ secret: s.secret, qr: await QRCode.toDataURL(s.otpAuthUri, { margin: 1, width: 200 }) }))
      .catch(e => setError(e instanceof ApiError ? e.message : 'Could not start the setup.'))
  }, [])

  const confirm = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    try {
      onEnabled(await api.twoFactorEnable(code))
    } catch (err) {
      setError(err instanceof ApiError ? Object.values(err.fieldErrors).flat()[0] ?? err.message : 'Not enabled.')
    }
  }

  if (!setup) return error ? <p className="error">{error}</p> : <p className="muted">Preparing…</p>
  return (
    <form className="two-factor-setup" onSubmit={confirm}>
      <ol className="steps">
        <li>Open an authenticator app on your phone (Microsoft or Google Authenticator, 1Password, Authy…).</li>
        <li>
          Scan this code, or type the key by hand.
          <div className="qr-row">
            <img src={setup.qr} alt="QR code for the authenticator app" width={200} height={200} />
            <code className="secret">{setup.secret.match(/.{1,4}/g)!.join(' ')}</code>
          </div>
        </li>
        <li>
          Enter the 6-digit code the app shows.
          <div className="code-row">
            <input inputMode="numeric" autoComplete="one-time-code" maxLength={7} value={code} onChange={e => setCode(e.target.value)}
              placeholder="123 456" aria-label="Code from the app" autoFocus required />
            <button className="primary">Turn on</button>
          </div>
        </li>
      </ol>
      {error && <p className="error">{error}</p>}
    </form>
  )
}
