import { useState, useCallback } from 'react'
import api from '../../../../services/api'
import { useFinancialAnalyticsSource } from './useFinancialAnalyticsSource'
import { financialAnalyticsContracts as contracts } from '../financialAnalyticsContracts'

export function useCampaignCosts(period = 'last_30_days') {
    return useFinancialAnalyticsSource('/admin/analytics/campaign-costs', { period }, contracts.costs)
}

// Existing versioned mutation contracts are unchanged.
export function useCreateCampaignCost() {
    const [loading, setLoading] = useState(false)
    const [error, setError] = useState(null)
    const createCost = useCallback(async (costData) => {
        try {
            setLoading(true)
            setError(null)
            const response = await api.post('/admin/analytics/campaign-costs', costData)
            return response.data
        } catch (err) {
            const message = err.response?.data?.message || 'Failed to create campaign cost'
            setError(message)
            throw new Error(message)
        } finally {
            setLoading(false)
        }
    }, [])
    return { createCost, loading, error }
}

export function useUpdateCampaignCost() {
    const [loading, setLoading] = useState(false)
    const [error, setError] = useState(null)
    const updateCost = useCallback(async (id, costData, revision) => {
        try {
            setLoading(true)
            setError(null)
            const response = await api.put(`/admin/analytics/campaign-costs/${id}`, costData, { headers: revision ? { 'If-Match': revision } : {} })
            return response.data
        } catch (err) {
            const message = err.response?.data?.message || 'Failed to update campaign cost'
            setError(message)
            throw new Error(message)
        } finally {
            setLoading(false)
        }
    }, [])
    return { updateCost, loading, error }
}

export function useDeleteCampaignCost() {
    const [loading, setLoading] = useState(false)
    const [error, setError] = useState(null)
    const deleteCost = useCallback(async (id) => {
        try {
            setLoading(true)
            setError(null)
            await api.delete(`/admin/analytics/campaign-costs/${id}`)
            return true
        } catch (err) {
            const message = err.response?.data?.message || 'Failed to delete campaign cost'
            setError(message)
            throw new Error(message)
        } finally {
            setLoading(false)
        }
    }, [])
    return { deleteCost, loading, error }
}

export function useRoiSummary(model = 'LastTouch', period = 'last_30_days') {
    return useFinancialAnalyticsSource('/admin/analytics/roi/summary', { model, period }, contracts.roiSummary)
}
export function useRoiByCampaign(model = 'LastTouch', period = 'last_30_days') {
    return useFinancialAnalyticsSource('/admin/analytics/roi/campaigns', { model, period }, contracts.roiCampaigns)
}
