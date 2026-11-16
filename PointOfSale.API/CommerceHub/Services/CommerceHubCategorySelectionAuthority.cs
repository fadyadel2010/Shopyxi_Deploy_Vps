using Microsoft.EntityFrameworkCore;
using PointOfSale.API.CommerceHub.Handlers;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Reliability;

namespace PointOfSale.API.CommerceHub.Services;

public static class CommerceHubCategorySelectionAuthority
{
    public static async Task ValidateAsync(
        ApplicationDbContext dbContext,
        Guid? parentCategoryId,
        Guid selectedCategoryId,
        CancellationToken cancellationToken)
    {
        if (!parentCategoryId.HasValue || parentCategoryId.Value == Guid.Empty)
        {
            throw new CommerceHubValidationException("A CommerceHub main category is required.");
        }

        var parentId = parentCategoryId.Value;
        var categories = await dbContext.CommerceHubCategories
            .AsNoTracking()
            .Where(category => category.Id == parentId || category.Id == selectedCategoryId || category.ParentId == parentId)
            .ToListAsync(cancellationToken);
        var parent = categories.FirstOrDefault(category => category.Id == parentId);
        var selected = categories.FirstOrDefault(category => category.Id == selectedCategoryId);

        if (parent == null || !parent.IsActive || parent.ParentId.HasValue ||
            !await CommerceHubCategoryLineageAuthority.IsLiveAsync(dbContext, parentId, cancellationToken))
        {
            throw new CommerceHubValidationException("The selected CommerceHub main category is not active.");
        }

        if (selected == null || !selected.IsActive ||
            !await CommerceHubCategoryLineageAuthority.IsLiveAsync(dbContext, selectedCategoryId, cancellationToken))
        {
            throw new CommerceHubValidationException("The selected CommerceHub category is not active.");
        }

        var activeChildren = categories.Where(category => category.ParentId == parentId && category.IsActive).ToList();
        if (activeChildren.Count == 0)
        {
            if (selectedCategoryId != parentId)
            {
                throw new CommerceHubValidationException("This main category has no subcategory; publish against the main category.");
            }
            return;
        }

        if (selectedCategoryId == parentId || selected.ParentId != parentId)
        {
            throw new CommerceHubValidationException("Select an active subcategory that belongs to the selected main category.");
        }
    }
}
