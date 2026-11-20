import { describe, expect, it, vi } from 'vitest'
import { superAdminApiClient } from '../core/superAdminApiClient'
import { downloadSuperAdminOrdersCsv } from './ordersService'
vi.mock('../core/superAdminApiClient',()=>({superAdminApiClient:{getBlob:vi.fn()}}))

describe('Orders export transport',()=>{
    it('uses the authenticated API route, sends all filters and excludes page limits',async()=>{
        vi.mocked(superAdminApiClient.getBlob).mockResolvedValue(new Blob(['header'],{type:'text/csv;charset=utf-8'}))
        await downloadSuperAdminOrdersCsv({page:3,pageSize:20,search:' Arabic اسم ',status:'Pending',
            paymentStatus:'Unpaid',tenantId:'fixture-store',fromDate:'2026-11-01',toDate:'2026-11-16',
            sortBy:'TotalAmount',sortDirection:'Asc'})
        const url = new URL(vi.mocked(superAdminApiClient.getBlob).mock.lastCall![0],'https://admin.example.test')
        expect(url.pathname).toBe('/api/super-admin/orders/export')
        expect(url.searchParams.get('search')).toBe('Arabic اسم')
        expect(url.searchParams.get('status')).toBe('Pending')
        expect(url.searchParams.get('tenantId')).toBe('fixture-store')
        expect(url.searchParams.has('page')).toBe(false)
        expect(url.searchParams.has('pageSize')).toBe(false)
    })
    it('rejects a successful HTML SPA fallback instead of downloading it as orders CSV',async()=>{
        vi.mocked(superAdminApiClient.getBlob).mockResolvedValue(new Blob(['<html>'],{type:'text/html'}))
        await expect(downloadSuperAdminOrdersCsv({page:1,pageSize:20,sortBy:'CreatedAt',sortDirection:'Desc'})).rejects.toThrow(/CSV/)
    })
})
