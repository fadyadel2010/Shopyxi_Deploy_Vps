import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import OpenCloseCashboxModal from './OpenCloseCashboxModal'

const apiPostMock = vi.hoisted(() => vi.fn())
const showSuccessMock = vi.hoisted(() => vi.fn())
const showErrorMock = vi.hoisted(() => vi.fn())

vi.mock('../services/api', () => ({
    default: { post: apiPostMock },
}))

vi.mock('../components/Toast', () => ({
    useToast: () => ({ showSuccess: showSuccessMock, showError: showErrorMock }),
}))

vi.mock('../hooks/useFormatting', () => ({
    useFormatting: () => ({
        dir: 'ltr',
        isRTL: false,
        formatCurrency: value => `EGP ${Number(value).toFixed(2)}`,
        formatSignedCurrency: value => `${Number(value) >= 0 ? '+' : ''}${Number(value).toFixed(2)}`,
    }),
}))

vi.mock('react-i18next', () => ({
    useTranslation: () => ({
        t: (key, fallback) => ({
            'cashbox.openCashbox': 'Open Cashbox',
            'cashbox.closeCashbox': 'Close Cashbox',
            'cashbox.zeroOpeningPolicy': 'This cashbox will open with zero new cash.',
            'cashbox.actualCashCount': 'Actual cash count',
            'cashbox.expectedBalance': 'Expected balance',
            'cashbox.notesLabel': 'Notes',
            'cashbox.notesRequiredInline': 'Notes required',
            'cashbox.closingNotesPlaceholder': 'Closing notes',
            'cashbox.openingNotesPlaceholder': 'Opening notes',
            'cashbox.closedWithDifference': 'Closed with difference',
            'cashbox.openedSuccessfully': 'Opened',
            'common.cancel': 'Cancel',
        }[key] ?? fallback ?? key),
    }),
}))

beforeEach(() => {
    vi.clearAllMocks()
    apiPostMock.mockResolvedValue({ data: {} })
})

describe('OpenCloseCashboxModal cash authority', () => {
    it('opens with zero new cash instead of asking for a balance the API must reject', async () => {
        const user = userEvent.setup()
        const onSuccess = vi.fn()
        render(
            <OpenCloseCashboxModal
                cashbox={{ id: 'cashbox-1', expectedBalance: 0 }}
                mode="open"
                onClose={vi.fn()}
                onSuccess={onSuccess}
            />,
        )

        expect(screen.getByText(/open with zero new cash/i)).toBeInTheDocument()
        expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument()
        await user.click(screen.getByRole('button', { name: 'Open Cashbox' }))

        await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/cashbox/cashbox-1/open', {
            openingBalance: 0,
            notes: null,
        }))
        expect(onSuccess).toHaveBeenCalledOnce()
    })

    it('requires an explanatory note before submitting a material counted variance', async () => {
        const user = userEvent.setup()
        render(
            <OpenCloseCashboxModal
                cashbox={{ id: 'cashbox-1', expectedBalance: 100 }}
                mode="close"
                onClose={vi.fn()}
                onSuccess={vi.fn()}
            />,
        )

        await user.type(screen.getByLabelText('Actual cash count *'), '90')
        const notes = screen.getByPlaceholderText('Closing notes')
        expect(notes).toBeRequired()
        await user.click(screen.getByRole('button', { name: 'Close Cashbox' }))
        expect(apiPostMock).not.toHaveBeenCalled()

        await user.type(notes, 'Counted twice by shift manager')
        await user.click(screen.getByRole('button', { name: 'Close Cashbox' }))

        await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/cashbox/cashbox-1/close', {
            actualCash: 90,
            notes: 'Counted twice by shift manager',
        }))
    })
})
