import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import CommissionPreviewPanel from './CommissionPreviewPanel'

describe('commission authority guidance', () => {
    it('explains SQL fail-closed authority instead of promising legacy fallback', () => {
        render(<CommissionPreviewPanel categories={[]} vendors={[]} getAppliedRuleLabel={() => ''}
            isLoadingLookups={false} isSubmitting={false} onPreview={vi.fn()} />)
        expect(screen.getByText(/Checkout is blocked when no active SQL rule matches/)).toBeInTheDocument()
        expect(screen.queryByText(/legacy fallback applies only/)).not.toBeInTheDocument()
    })
})
