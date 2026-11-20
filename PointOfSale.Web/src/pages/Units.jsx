import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Plus, Sprout } from 'lucide-react'
import api from '../services/api'
import { useConfirmDialog } from '../common/confirm/ConfirmDialogProvider'
import { useToast } from '../components/Toast'
import { useRTL } from '../hooks/useRTL'
import { useHasPermission } from '../hooks/usePosSessionPermission'
import {
    Badge,
    Button,
    Card,
    Dialog,
    Field,
    Input,
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from '../design-system/primitives'
import './Units.css'

const UNIT_FORM_ID = 'pos-unit-form'

export default function Units() {
    const { t } = useTranslation()
    const { dir } = useRTL()
    const { confirm } = useConfirmDialog()
    const { showSuccess, showError } = useToast()
    const canManageCatalog = useHasPermission('catalog.manage')
    const canDeleteCatalog = useHasPermission('catalog.delete')
    const [units, setUnits] = useState([])
    const [loading, setLoading] = useState(true)
    const [showModal, setShowModal] = useState(false)
    const [editingUnit, setEditingUnit] = useState(null)
    const [formData, setFormData] = useState({
        name: '',
        symbol: '',
        description: '',
        isActive: true
    })
    const dialogOpenerRef = useRef(null)
    const nameInputRef = useRef(null)

    const fetchUnits = useCallback(async () => {
        try {
            setLoading(true)
            const response = await api.get('/units?includeInactive=true')
            setUnits(response.data)
        } catch (error) {
            console.error('Error fetching units:', error)
            showError(t('units.failedToLoad'))
        } finally {
            setLoading(false)
        }
    }, [showError, t])

    useEffect(() => {
        fetchUnits()
    }, [fetchUnits])

    const handleAdd = (event) => {
        dialogOpenerRef.current = event?.currentTarget ?? null
        setEditingUnit(null)
        setFormData({
            name: '',
            symbol: '',
            description: '',
            isActive: true
        })
        setShowModal(true)
    }

    const handleEdit = (unit, event) => {
        dialogOpenerRef.current = event?.currentTarget ?? null
        setEditingUnit(unit)
        setFormData({
            name: unit.name,
            symbol: unit.symbol,
            description: unit.description || '',
            isActive: unit.isActive
        })
        setShowModal(true)
    }

    const handleSubmit = async (e) => {
        e.preventDefault()

        try {
            if (editingUnit) {
                await api.put(`/units/${editingUnit.id}`, formData)
                showSuccess(t('units.updated'))
            } else {
                await api.post('/units', formData)
                showSuccess(t('units.created'))
            }

            setShowModal(false)
            fetchUnits()
        } catch (error) {
            console.error('Error saving unit:', error)
            showError(error.response?.data || t('units.failedToSave'))
        }
    }

    const handleDelete = async (id) => {
        const confirmed = await confirm({
            confirmLabel: t('common.delete'),
            description: t('units.deleteConfirm'),
            title: t('common.warning'),
            variant: 'danger',
        })

        if (!confirmed) return

        try {
            await api.delete(`/units/${id}`)
            showSuccess(t('units.deleted'))
            fetchUnits()
        } catch (error) {
            console.error('Error deleting unit:', error)
            showError(error.response?.data || t('units.failedToDelete'))
        }
    }

    const handleSeedDefaults = async () => {
        const confirmed = await confirm({
            confirmLabel: t('common.confirm'),
            description: t('units.seedDefaultsConfirm'),
            title: t('common.warning'),
            variant: 'warning',
        })

        if (!confirmed) return

        try {
            const response = await api.post('/units/seed-defaults')
            showSuccess(response.data.message || t('units.defaultsSeeded'))
            fetchUnits()
        } catch (error) {
            console.error('Error seeding defaults:', error)
            showError(t('units.failedToSeedDefaults'))
        }
    }

    const handleCloseModal = () => {
        setShowModal(false)
        setEditingUnit(null)
        setFormData({
            name: '',
            symbol: '',
            description: '',
            isActive: true
        })
    }

    return (
        <div dir={dir} className="pos-units">
            <header className="pos-units__header">
                <div className="pos-units__heading">
                    <h1 className="pos-units__title">{t('units.title')}</h1>
                    <p className="pos-units__subtitle">{t('units.subtitle')}</p>
                </div>
                {canManageCatalog && (
                    <div className="pos-units__actions">
                        <Button
                            variant="secondary"
                            leadingIcon={<Sprout size={16} />}
                            onClick={handleSeedDefaults}
                        >
                            {t('units.seedDefaults')}
                        </Button>
                        <Button leadingIcon={<Plus size={16} />} onClick={handleAdd}>
                            {t('units.addUnit')}
                        </Button>
                    </div>
                )}
            </header>

            {loading ? (
                <Card padding="lg" className="pos-units__state" role="status">
                    <p className="pos-units__state-text">{t('common.loading')}</p>
                </Card>
            ) : units.length === 0 ? (
                <Card padding="lg" className="pos-units__state">
                    <p className="pos-units__state-text">{t('units.noUnits')}</p>
                    {canManageCatalog && (
                        <Button onClick={handleAdd}>{t('units.addFirstUnit')}</Button>
                    )}
                </Card>
            ) : (
                <Table aria-label={t('units.title')}>
                    <TableHeader>
                        <TableRow>
                            <TableHead>{t('units.name')}</TableHead>
                            <TableHead>{t('units.symbol')}</TableHead>
                            <TableHead>{t('units.description')}</TableHead>
                            <TableHead>{t('common.status')}</TableHead>
                            <TableHead className="pos-units__actions-column">{t('common.actions')}</TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {units.map((unit) => (
                            <TableRow key={unit.id}>
                                <TableCell className="pos-units__name">{unit.name}</TableCell>
                                <TableCell className="pos-units__symbol">{unit.symbol}</TableCell>
                                <TableCell className="pos-units__description">
                                    {unit.description || '—'}
                                </TableCell>
                                <TableCell>
                                    <Badge variant={unit.isActive ? 'success' : 'danger'} dot>
                                        {unit.isActive ? t('common.active') : t('common.inactive')}
                                    </Badge>
                                </TableCell>
                                <TableCell className="pos-units__actions-column">
                                    <div className="pos-units__row-actions">
                                        {canManageCatalog && (
                                            <Button
                                                variant="ghost"
                                                size="sm"
                                                onClick={(event) => handleEdit(unit, event)}
                                            >
                                                {t('common.edit')}
                                            </Button>
                                        )}
                                        {canDeleteCatalog && (
                                            <Button
                                                variant="ghost"
                                                size="sm"
                                                className="pos-units__danger-action"
                                                onClick={() => handleDelete(unit.id)}
                                            >
                                                {t('common.delete')}
                                            </Button>
                                        )}
                                    </div>
                                </TableCell>
                            </TableRow>
                        ))}
                    </TableBody>
                </Table>
            )}

            {canManageCatalog && (
                <Dialog
                    open={showModal}
                    onOpenChange={(open) => {
                        if (!open) handleCloseModal()
                    }}
                    title={editingUnit ? t('units.editUnit') : t('units.addNewUnit')}
                    closeLabel={t('common.close')}
                    initialFocusRef={nameInputRef}
                    restoreFocusRef={dialogOpenerRef}
                    footer={(
                        <>
                            <Button variant="secondary" onClick={handleCloseModal}>
                                {t('common.cancel')}
                            </Button>
                            <Button type="submit" form={UNIT_FORM_ID}>
                                {editingUnit ? t('common.update') : t('common.create')}
                            </Button>
                        </>
                    )}
                >
                    <form id={UNIT_FORM_ID} className="pos-units__form" onSubmit={handleSubmit}>
                        <Input
                            ref={nameInputRef}
                            label={t('units.name')}
                            value={formData.name}
                            onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                            placeholder={t('units.namePlaceholder')}
                            required
                        />
                        <Input
                            label={t('units.symbol')}
                            value={formData.symbol}
                            onChange={(e) => setFormData({ ...formData, symbol: e.target.value })}
                            placeholder={t('units.symbolPlaceholder')}
                            required
                        />
                        <Field id="pos-unit-description" label={t('units.description')}>
                            {({ id }) => (
                                <textarea
                                    id={id}
                                    className="pos-input pos-units__textarea"
                                    rows={3}
                                    value={formData.description}
                                    onChange={(e) => setFormData({ ...formData, description: e.target.value })}
                                    placeholder={t('units.descriptionPlaceholder')}
                                />
                            )}
                        </Field>
                        <label className="pos-units__checkbox">
                            <input
                                type="checkbox"
                                checked={formData.isActive}
                                onChange={(e) => setFormData({ ...formData, isActive: e.target.checked })}
                            />
                            <span>{t('units.isActive')}</span>
                        </label>
                    </form>
                </Dialog>
            )}
        </div>
    )
}
