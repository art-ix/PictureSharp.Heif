/* Independent test driver for the public libheif API; never linked into the managed plugin. */
#include <libheif/heif.h>
#include <stdio.h>
#include <stdlib.h>

static void check(heif_error error)
{
    if (error.code != heif_error_Ok) {
        fprintf(stderr, "%s\n", error.message);
        exit(1);
    }
}

int main(int argc, char** argv)
{
    if (argc != 3) return 2;
    heif_context* context = heif_context_alloc();
    check(heif_context_read_from_file(context, argv[1], NULL));
    heif_image_handle* primary = NULL;
    check(heif_context_get_primary_image_handle(context, &primary));
    if (heif_image_handle_get_number_of_thumbnails(primary) != 1) return 3;
    heif_item_id id = 0;
    if (heif_image_handle_get_list_of_thumbnail_IDs(primary, &id, 1) != 1) return 4;
    heif_image_handle* thumbnail = NULL;
    check(heif_image_handle_get_thumbnail(primary, id, &thumbnail));
    heif_image* image = NULL;
    check(heif_decode_image(thumbnail, &image, heif_colorspace_RGB, heif_chroma_interleaved_RGBA, NULL));
    int width = heif_image_get_width(image, heif_channel_interleaved);
    int height = heif_image_get_height(image, heif_channel_interleaved);
    size_t stride = 0;
    const unsigned char* pixels = heif_image_get_plane_readonly2(image, heif_channel_interleaved, &stride);
    if (!pixels || width <= 0 || height <= 0 || stride < (size_t)width * 4) return 5;
    FILE* output = fopen(argv[2], "wb");
    if (!output) return 6;
    for (int y = 0; y < height; y++) {
        if (fwrite(pixels + (size_t)y * stride, 4, (size_t)width, output) != (size_t)width) return 7;
    }
    if (fclose(output)) return 8;
    printf("%u %d %d\n", id, width, height);
    heif_image_release(image);
    heif_image_handle_release(thumbnail);
    heif_image_handle_release(primary);
    heif_context_free(context);
    return 0;
}
