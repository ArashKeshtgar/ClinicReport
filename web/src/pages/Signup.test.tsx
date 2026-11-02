import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import Signup, { suggestSlug } from './Signup'

describe('suggestSlug', () => {
  it('turns a clinic name into an address', () => {
    expect(suggestSlug("St. Mary's Heart Clinic")).toBe('st-marys-heart-clinic')
    expect(suggestSlug('  Clinique Écho — Montréal ')).toBe('clinique-echo-montreal')
    expect(suggestSlug('a'.repeat(39) + ' b')).toBe('a'.repeat(39))
  })
})

describe('Signup', () => {
  afterEach(() => vi.restoreAllMocks())

  it('suggests the address from the name and sends the form', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (url, init) => {
      if (String(url).startsWith('/api/signup/slug'))
        return new Response(JSON.stringify({ slug: 'harbour-cardiology', available: true, problem: null }))
      expect(JSON.parse(String(init?.body))).toMatchObject({ clinicName: 'Harbour Cardiology', slug: 'harbour-cardiology', email: 'owner@example.com' })
      return new Response(JSON.stringify({ slug: 'harbour-cardiology', url: 'http://harbour-cardiology.example.test/', trialEndsAt: '2026-11-16T00:00:00Z' }), { status: 201 })
    })
    render(<Signup baseDomain="example.test" />)

    await userEvent.type(screen.getByLabelText('Clinic name'), 'Harbour Cardiology')
    expect(screen.getByLabelText(/Address/)).toHaveValue('harbour-cardiology')
    await userEvent.type(screen.getByLabelText('Your name'), 'Test Owner')
    await userEvent.type(screen.getByLabelText(/^Email/), 'owner@example.com')
    await userEvent.type(screen.getByLabelText('Password'), 'a long passphrase 42')
    await userEvent.click(screen.getByRole('button', { name: 'Create my clinic' }))

    expect(await screen.findByRole('heading', { name: 'Your clinic is ready' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Go to Harbour Cardiology' })).toHaveAttribute('href', 'http://harbour-cardiology.example.test/')
    expect(fetchMock).toHaveBeenCalledWith('/api/signup', expect.objectContaining({ method: 'POST' }))
  })
})
