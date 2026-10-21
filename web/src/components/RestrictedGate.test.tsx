import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ApiError, isRestricted } from '../api'
import { RestrictedGate } from './RestrictedGate'

describe('RestrictedGate', () => {
  it('names the label and asks for a reason of at least ten characters before it opens anything', async () => {
    const onOpen = vi.fn(async () => {})
    render(<RestrictedGate label="V" onOpen={onOpen} />)
    expect(screen.getByRole('heading', { name: 'Very restricted' })).toBeInTheDocument()
    const open = screen.getByRole('button', { name: 'Open this report' })
    expect(open).toBeDisabled()

    await userEvent.type(screen.getByLabelText(/Why do you need to open it/), 'short')
    expect(open).toBeDisabled()
    await userEvent.type(screen.getByLabelText(/Why do you need to open it/), ' but now long enough ')
    expect(open).toBeEnabled()
    await userEvent.click(open)
    expect(onOpen).toHaveBeenCalledWith('short but now long enough')   // trimmed
  })

  it('says R is restricted and shows a refusal instead of opening', async () => {
    render(<RestrictedGate label="R" onOpen={async () => { throw new Error('No permission.') }} />)
    expect(screen.getByRole('heading', { name: 'Restricted' })).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText(/Why do you need to open it/), 'Covering the reader today')
    await userEvent.click(screen.getByRole('button', { name: 'Open this report' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('No permission.')
  })
})

describe('isRestricted', () => {
  it('is only the 403 that says so', () => {
    expect(isRestricted(new ApiError(403, 'x', {}, { code: 'restricted', label: 'V' }))).toBe(true)
    expect(isRestricted(new ApiError(403, 'x', {}, {}))).toBe(false)
    expect(isRestricted(new ApiError(404, 'x', {}, { code: 'restricted' }))).toBe(false)
    expect(isRestricted(new Error('x'))).toBe(false)
  })
})
