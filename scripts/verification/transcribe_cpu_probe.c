// Verification only. Compile against the pinned transcribe.cpp 0.4.1 header.
// Run in a disposable, GPU-free container, optionally under qemu-x86_64.
#include "transcribe.h"
#include <cpuid.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void check(transcribe_status status) {
    if (status != TRANSCRIBE_OK) {
        fprintf(stderr, "Native failure: %s\n", transcribe_status_string(status));
        exit(2);
    }
}

int main(int argc, char **argv) {
    if (argc != 4) return 2;
    unsigned a, b, c, d;
    if (!__get_cpuid(1, &a, &b, &c, &d)) return 2;
    printf("cpuid sse2=%u sse4.2=%u avx=%u fma=%u\n", !!(d & bit_SSE2), !!(c & bit_SSE4_2), !!(c & bit_AVX), !!(c & bit_FMA));
    unsetenv("GGML_BACKEND_PATH");
    setenv("TRANSCRIBE_BACKENDS", "cpu", 1);
    struct transcribe_backend_init_params init;
    transcribe_backend_init_params_init(&init);
    init.artifact_dir = argv[1];
    init.allowed_backends = TRANSCRIBE_BACKEND_MASK_CPU;
    check(transcribe_init_backends_ex(&init));
    if (transcribe_backend_available(TRANSCRIBE_BACKEND_VULKAN) || transcribe_backend_available(TRANSCRIBE_BACKEND_CUDA)) return 3;
    struct transcribe_model_load_params model;
    transcribe_model_load_params_init(&model);
    model.backend = TRANSCRIBE_BACKEND_CPU;
    model.device = transcribe_device_get(0);
    model.no_mmap = true;
    if (!model.device) return 4;
    struct transcribe_session_params params;
    transcribe_session_params_init(&params);
    params.n_threads = 2;
    struct transcribe_session *session = NULL;
    check(transcribe_open(argv[2], &model, &params, &session));
    const char *backend = transcribe_model_backend(transcribe_get_model(session));
    printf("active_backend=%s\n", backend);
    if (!backend || strcmp(backend, "CPU")) return 4;
    FILE *audio = fopen(argv[3], "rb");
    if (!audio) return 5;
    fseek(audio, 0, SEEK_END);
    long bytes = ftell(audio);
    rewind(audio);
    if (bytes <= 0 || bytes % sizeof(float)) return 6;
    float *pcm = malloc(bytes);
    if (!pcm || fread(pcm, 1, bytes, audio) != (size_t)bytes) return 6;
    fclose(audio);
    struct transcribe_run_params run;
    transcribe_run_params_init(&run);
    run.language = "en";
    check(transcribe_run(session, pcm, bytes / sizeof(float), &run));
    const char *text = transcribe_full_text(session);
    printf("transcript=%s\n", text);
    int ok = text && strstr(text, "bent") && strstr(text, "hair");
    FILE *maps = fopen("/proc/self/maps", "r");
    char line[2048];
    if (!maps) ok = 0;
    while (maps && fgets(line, sizeof line, maps)) {
        if (strstr(line, "ggml-cpu") || strstr(line, "vulkan") || strstr(line, "cuda")) fputs(line, stdout);
        if (strstr(line, "libvulkan") || strstr(line, "libggml-vulkan") || strstr(line, "libcuda")) ok = 0;
    }
    if (maps) fclose(maps);
    free(pcm);
    transcribe_session_free(session);
    return ok ? 0 : 7;
}
