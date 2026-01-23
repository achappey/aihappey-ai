using AIHappey.Core.Models;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Bria;

public partial class BriaProvider : IModelProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return BriaImageModels;
    }

    public static IReadOnlyList<Model> BriaImageModels =>
    [
        new()
        {
            // Maps to POST /v2/image/generate
            Id = "bria/generate",
            Name = "Bria FIDO",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            // Maps to POST /v2/image/generate/lite
            Id = "bria/generate/lite",
            Name = "Bria FIDO lite",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            // Maps to POST /v2/image/edit
            Id = "bria/edit",
            Name = "Bria Image Edit",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            // Maps to POST /v2/image/edit/add_object_by_text
            Id = "bria/edit/add_object_by_text",
            Name = "Bria Add Object",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            // Maps to POST /v2/image/edit/replace_object_by_text
            Id = "bria/edit/replace_object_by_text",
            Name = "Bria Replace Object",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/erase_by_text",
            Name = "Bria Erase Object",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/blend",
            Name = "Bria Image Blending",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/reseason",
            Name = "Bria Reseason Image",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/replace_text",
            Name = "Bria Rewrite",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/sketch_to_image",
            Name = "Bria Sketch to Image",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/restore",
            Name = "Bria Restore Old Image",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/colorize",
            Name = "Bria Colorize",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/restyle",
            Name = "Bria Restyle Image",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/relight",
            Name = "Bria Relight Image",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/erase",
            Name = "Bria Eraser",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/gen_fill",
            Name = "Bria Generative Fill",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/remove_background",
            Name = "Bria Remove Background",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/replace_background",
            Name = "Bria Replace Background",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/erase_foreground",
            Name = "Bria Erase Foreground",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/blur_background",
            Name = "Bria Blur Background",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/expand",
            Name = "Bria Expand Image",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/enhance",
            Name = "Bria Enhance Image",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/increase_resolution",
            Name = "Bria Increase Resolution",
            Type = "image",
            OwnedBy = "Bria"
        },
        new()
        {
            Id = "bria/edit/crop_foreground",
            Name = "Bria Crop out Foreground",
            Type = "image",
            OwnedBy = "Bria"
        }

    ];

}
