import { render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Settings } from '../api'
import VerifyEmail from './VerifyEmail'

const settings = { name: 'Harbour Cardiology' } as Settings

describe('VerifyEmail', () => {
  afterEach(() => { vi.restoreAllMocks(); window.history.replaceState(null, '', '/') })

  it('sends the token from the link and says the address is confirmed', async () => {
    window.history.replaceState(null, '', '/verify-email?token=abc_123-x')
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ verified: true })))
    const onVerified = vi.fn()
    render(<VerifyEmail settings={settings} onVerified={onVerified} />)

    expect(await screen.findByText(/email address is confirmed/)).toBeInTheDocument()
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({ token: 'abc_123-x' })
    expect(onVerified).toHaveBeenCalled()
  })

  it('shows why a link does not work', async () => {
    window.history.replaceState(null, '', '/verify-email?token=old')
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(
      JSON.stringify({ title: 'Bad Request', detail: 'This link is wrong, used or expired.' }), { status: 400 }))
    render(<VerifyEmail settings={settings} onVerified={vi.fn()} />)

    expect(await screen.findByRole('alert')).toHaveTextContent('This link is wrong, used or expired.')
  })
})
