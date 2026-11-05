import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import OperatorConsole from './Operator'

const tenant = (over: object = {}) => ({
  id: 1, slug: 'north', name: 'North Heart', plan: 'Pro', status: 'Active', createdAt: '2026-11-01T00:00:00Z', activeUsers: 4, signedReports: 12,
  lastActivityAt: '2026-11-04T00:00:00Z', databaseBytes: 3145728, databaseFailed: false, deletedAt: null, canDelete: false, ...over,
})

const serve = (routes: Record<string, unknown>) => vi.spyOn(globalThis, 'fetch').mockImplementation(async input => {
  const url = String(input)
  if (!(url in routes)) return new Response(null, { status: 404 })
  const body = routes[url]
  return body === null ? new Response(null, { status: 204 }) : new Response(JSON.stringify(body))
})

describe('OperatorConsole', () => {
  afterEach(() => vi.restoreAllMocks())

  it('asks for a password and a code when signed out', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 401 }))
    render(<OperatorConsole />)

    expect(await screen.findByLabelText('Two-factor code')).toBeRequired()
    expect(screen.getByLabelText('Password')).toBeRequired()
  })

  it('lists clinics with their usage and suspends one', async () => {
    const fetchMock = serve({
      '/api/operator/me': { userName: 'ops' }, '/api/operator/tenants': [tenant()], '/api/operator/events': [],
      '/api/operator/tenants/1/suspend': null,
    })
    render(<OperatorConsole />)

    expect(await screen.findByText('North Heart')).toBeInTheDocument()
    expect(screen.getByText('3.0 MB')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Suspend' }))

    expect(fetchMock).toHaveBeenCalledWith('/api/operator/tenants/1/suspend', expect.objectContaining({ method: 'POST' }))
  })

  it('offers Resume to a suspended clinic and nothing to a cancelled one', async () => {
    serve({
      '/api/operator/me': { userName: 'ops' }, '/api/operator/events': [],
      '/api/operator/tenants': [tenant({ status: 'Suspended' }), tenant({ id: 2, slug: 'gone', name: 'Gone', status: 'Cancelled' })],
    })
    render(<OperatorConsole />)

    expect(await screen.findByRole('button', { name: 'Resume' })).toBeInTheDocument()
    expect(screen.getAllByRole('button').map(b => b.textContent)).toEqual(['Sign out', 'Resume'])
  })

  it('sends a deletion only with what was typed to confirm it', async () => {
    const fetchMock = serve({
      '/api/operator/me': { userName: 'ops' }, '/api/operator/events': [],
      '/api/operator/tenants': [tenant({ status: 'Cancelled', canDelete: true })], '/api/operator/tenants/1/delete': null,
    })
    const prompt = vi.spyOn(window, 'prompt').mockReturnValueOnce(null).mockReturnValueOnce('north')
    render(<OperatorConsole />)

    await userEvent.click(await screen.findByRole('button', { name: 'Delete data' }))
    expect(fetchMock).not.toHaveBeenCalledWith('/api/operator/tenants/1/delete', expect.anything())

    await userEvent.click(screen.getByRole('button', { name: 'Delete data' }))
    expect(prompt).toHaveBeenCalledTimes(2)
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/api/operator/tenants/1/delete',
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ confirmSlug: 'north' }) })))
  })
})
