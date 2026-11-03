#define _GNU_SOURCE

#include <errno.h>
#include <fcntl.h>
#include <ftw.h>
#include <linux/openat2.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <unistd.h>

#ifndef RESOLVE_NO_XDEV
#define RESOLVE_NO_XDEV 0x01
#endif
#ifndef RESOLVE_NO_SYMLINKS
#define RESOLVE_NO_SYMLINKS 0x04
#endif
#ifndef RESOLVE_BENEATH
#define RESOLVE_BENEATH 0x08
#endif

#if !defined(SYS_openat2) && defined(__NR_openat2)
#define SYS_openat2 __NR_openat2
#endif

#ifndef SYS_openat2
#error "linux-allocation-probe requires Linux headers with the openat2 syscall number"
#endif

#define PAYLOAD_SIZE (64U * 1024U)
#define STREAM_CHUNK 4096U
#define MAX_STREAM_BYTES PAYLOAD_SIZE
#define RUN_ROOT_NAME "run-root"
#define SLOT_NAME "evidence.bin"
#define HARDLINK_NAME "evidence-alias.bin"

typedef enum {
    PROBE_OK = 0,
    PROBE_PARENT_SYMLINK,
    PROBE_PARENT_OWNER_CHANGED,
    PROBE_PARENT_MODE_CHANGED,
    PROBE_PARENT_IDENTITY_CHANGED,
    PROBE_ROOT_COLLISION,
    PROBE_ROOT_SYMLINK,
    PROBE_ROOT_OWNER_CHANGED,
    PROBE_ROOT_MODE_CHANGED,
    PROBE_ROOT_IDENTITY_CHANGED,
    PROBE_FILE_COLLISION,
    PROBE_FILE_SYMLINK,
    PROBE_FILE_HARDLINK,
    PROBE_FILE_OWNER_CHANGED,
    PROBE_FILE_MODE_CHANGED,
    PROBE_FILE_IDENTITY_CHANGED,
    PROBE_CONTENT_MISMATCH,
    PROBE_IO_FAILURE,
    PROBE_HARNESS_FAILURE
} ProbeStatus;

typedef enum {
    MUTATE_NONE = 0,
    MUTATE_PARENT_SUBSTITUTE,
    MUTATE_PARENT_MODE,
    MUTATE_PARENT_OWNER,
    MUTATE_FILE_COLLISION,
    MUTATE_FILE_SYMLINK,
    MUTATE_ROOT_MODE,
    MUTATE_ROOT_OWNER,
    MUTATE_FILE_MODE,
    MUTATE_FILE_OWNER,
    MUTATE_FILE_HARDLINK
} Mutation;

typedef struct {
    const char *name;
    ProbeStatus expected;
    Mutation mutation;
    bool parent_is_symlink;
    bool occupied_root_directory;
    bool symlink_root;
    bool add_neighbor;
} Scenario;

typedef struct {
    Mutation mutation;
    char parent_path[4096];
    char moved_parent_path[4096];
    int mutation_errno;
    bool applied;
} MutationContext;

typedef struct {
    struct stat identity;
    const char *contents;
    size_t length;
} NeighborRecord;

typedef struct {
    bool observed;
    bool passed;
    ProbeStatus actual;
} ScenarioResult;

static uint8_t payload[PAYLOAD_SIZE];

static const char *status_name(ProbeStatus status)
{
    switch (status) {
    case PROBE_OK: return "OK";
    case PROBE_PARENT_SYMLINK: return "PARENT_SYMLINK";
    case PROBE_PARENT_OWNER_CHANGED: return "PARENT_OWNER_CHANGED";
    case PROBE_PARENT_MODE_CHANGED: return "PARENT_MODE_CHANGED";
    case PROBE_PARENT_IDENTITY_CHANGED: return "PARENT_IDENTITY_CHANGED";
    case PROBE_ROOT_COLLISION: return "ROOT_COLLISION";
    case PROBE_ROOT_SYMLINK: return "ROOT_SYMLINK";
    case PROBE_ROOT_OWNER_CHANGED: return "ROOT_OWNER_CHANGED";
    case PROBE_ROOT_MODE_CHANGED: return "ROOT_MODE_CHANGED";
    case PROBE_ROOT_IDENTITY_CHANGED: return "ROOT_IDENTITY_CHANGED";
    case PROBE_FILE_COLLISION: return "FILE_COLLISION";
    case PROBE_FILE_SYMLINK: return "FILE_SYMLINK";
    case PROBE_FILE_HARDLINK: return "FILE_HARDLINK";
    case PROBE_FILE_OWNER_CHANGED: return "FILE_OWNER_CHANGED";
    case PROBE_FILE_MODE_CHANGED: return "FILE_MODE_CHANGED";
    case PROBE_FILE_IDENTITY_CHANGED: return "FILE_IDENTITY_CHANGED";
    case PROBE_CONTENT_MISMATCH: return "CONTENT_MISMATCH";
    case PROBE_IO_FAILURE: return "IO_FAILURE";
    case PROBE_HARNESS_FAILURE: return "HARNESS_FAILURE";
    }
    return "UNKNOWN_STATUS";
}

static int openat2_call(int dirfd, const char *path, uint64_t flags,
                        uint64_t mode, uint64_t resolve)
{
    struct open_how how = {
        .flags = flags,
        .mode = mode,
        .resolve = resolve
    };

    return (int)syscall(SYS_openat2, dirfd, path, &how, sizeof(how));
}

static int openat2_is_supported(void)
{
    int slash_fd = open("/", O_PATH | O_DIRECTORY | O_CLOEXEC);
    if (slash_fd < 0) {
        fprintf(stderr, "ERROR: cannot open filesystem root: %s\n", strerror(errno));
        return -1;
    }

    int probe_fd = openat2_call(slash_fd, ".", O_PATH | O_DIRECTORY | O_CLOEXEC,
                                0, RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS |
                                   RESOLVE_NO_XDEV);
    int saved_errno = errno;
    close(slash_fd);
    if (probe_fd >= 0) {
        close(probe_fd);
        return 0;
    }

    if (saved_errno == ENOSYS || saved_errno == EINVAL || saved_errno == EPERM) {
        fprintf(stderr,
                "UNSUPPORTED: openat2 with BENEATH|NO_SYMLINKS|NO_XDEV is unavailable "
                "or blocked (errno=%d: %s)\n",
                saved_errno, strerror(saved_errno));
    } else {
        fprintf(stderr, "ERROR: openat2 feature probe failed (errno=%d: %s)\n",
                saved_errno, strerror(saved_errno));
    }
    return (saved_errno == ENOSYS || saved_errno == EINVAL || saved_errno == EPERM)
               ? 77
               : -1;
}

static bool same_inode(const struct stat *left, const struct stat *right)
{
    return left->st_dev == right->st_dev && left->st_ino == right->st_ino;
}

static const char *relative_absolute_path(const char *absolute_path)
{
    if (absolute_path == NULL || absolute_path[0] != '/') {
        return NULL;
    }
    while (*absolute_path == '/') {
        absolute_path++;
    }
    return absolute_path;
}

static ProbeStatus parent_policy_status(const struct stat *info)
{
    if (info->st_uid != geteuid()) {
        return PROBE_PARENT_OWNER_CHANGED;
    }
    if ((info->st_mode & 07777) != 0700) {
        return PROBE_PARENT_MODE_CHANGED;
    }
    return PROBE_OK;
}

static ProbeStatus verify_parent(int slash_fd, const char *parent_path,
                                 int parent_fd, const struct stat *bound_parent)
{
    struct stat retained;
    if (fstat(parent_fd, &retained) != 0) {
        return PROBE_IO_FAILURE;
    }

    ProbeStatus policy = parent_policy_status(&retained);
    if (policy != PROBE_OK) {
        return policy;
    }

    const char *relative_path = relative_absolute_path(parent_path);
    if (relative_path == NULL || *relative_path == '\0') {
        return PROBE_IO_FAILURE;
    }

    int current_fd = openat2_call(slash_fd, relative_path,
                                  O_RDONLY | O_DIRECTORY | O_CLOEXEC, 0,
                                  RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS);
    if (current_fd < 0) {
        if (errno == ELOOP) {
            return PROBE_PARENT_SYMLINK;
        }
        return PROBE_PARENT_IDENTITY_CHANGED;
    }

    struct stat current;
    ProbeStatus result = PROBE_IO_FAILURE;
    if (fstat(current_fd, &current) == 0) {
        if (!same_inode(bound_parent, &retained) ||
            !same_inode(bound_parent, &current)) {
            result = PROBE_PARENT_IDENTITY_CHANGED;
        } else {
            result = parent_policy_status(&current);
        }
    }
    close(current_fd);
    return result;
}

static ProbeStatus verify_root(int parent_fd, int root_fd, const char *root_name,
                               const struct stat *bound_root)
{
    struct stat retained;
    struct stat named;
    if (fstat(root_fd, &retained) != 0 ||
        fstatat(parent_fd, root_name, &named, AT_SYMLINK_NOFOLLOW) != 0) {
        return PROBE_ROOT_IDENTITY_CHANGED;
    }
    if (S_ISLNK(named.st_mode)) {
        return PROBE_ROOT_SYMLINK;
    }
    if (!S_ISDIR(retained.st_mode) || !S_ISDIR(named.st_mode) ||
        !same_inode(bound_root, &retained) || !same_inode(bound_root, &named)) {
        return PROBE_ROOT_IDENTITY_CHANGED;
    }
    if (retained.st_uid != geteuid()) {
        return PROBE_ROOT_OWNER_CHANGED;
    }
    if ((retained.st_mode & 07777) != 0700) {
        return PROBE_ROOT_MODE_CHANGED;
    }
    return PROBE_OK;
}

static ProbeStatus verify_file(int root_fd, int file_fd, const char *file_name,
                               const struct stat *bound_file)
{
    struct stat retained;
    struct stat named;
    if (fstat(file_fd, &retained) != 0 ||
        fstatat(root_fd, file_name, &named, AT_SYMLINK_NOFOLLOW) != 0) {
        return PROBE_FILE_IDENTITY_CHANGED;
    }
    if (S_ISLNK(named.st_mode)) {
        return PROBE_FILE_SYMLINK;
    }
    if (!S_ISREG(retained.st_mode) || !S_ISREG(named.st_mode) ||
        !same_inode(bound_file, &retained) || !same_inode(bound_file, &named)) {
        return PROBE_FILE_IDENTITY_CHANGED;
    }
    if (retained.st_nlink != 1) {
        return PROBE_FILE_HARDLINK;
    }
    if (retained.st_uid != geteuid()) {
        return PROBE_FILE_OWNER_CHANGED;
    }
    if ((retained.st_mode & 07777) != 0600) {
        return PROBE_FILE_MODE_CHANGED;
    }
    return PROBE_OK;
}

static ProbeStatus classify_root_collision(int parent_fd, const char *root_name)
{
    struct stat existing;
    if (fstatat(parent_fd, root_name, &existing, AT_SYMLINK_NOFOLLOW) != 0) {
        return PROBE_IO_FAILURE;
    }
    return S_ISLNK(existing.st_mode) ? PROBE_ROOT_SYMLINK : PROBE_ROOT_COLLISION;
}

static ProbeStatus classify_file_collision(int root_fd, const char *file_name)
{
    struct stat existing;
    if (fstatat(root_fd, file_name, &existing, AT_SYMLINK_NOFOLLOW) != 0) {
        return PROBE_IO_FAILURE;
    }
    if (S_ISLNK(existing.st_mode)) {
        return PROBE_FILE_SYMLINK;
    }
    if (S_ISREG(existing.st_mode) && existing.st_nlink > 1) {
        return PROBE_FILE_HARDLINK;
    }
    return PROBE_FILE_COLLISION;
}

static ProbeStatus stream_content_proof(int file_fd, const struct stat *bound_file)
{
    struct stat before;
    if (fstat(file_fd, &before) != 0) {
        return PROBE_IO_FAILURE;
    }
    if (!S_ISREG(before.st_mode) || !same_inode(bound_file, &before)) {
        return PROBE_FILE_IDENTITY_CHANGED;
    }
    if (before.st_size < 0 || (uint64_t)before.st_size > MAX_STREAM_BYTES ||
        (uint64_t)before.st_size != PAYLOAD_SIZE) {
        return PROBE_CONTENT_MISMATCH;
    }

    uint8_t buffer[STREAM_CHUNK];
    size_t offset = 0;
    while (offset < PAYLOAD_SIZE) {
        size_t requested = PAYLOAD_SIZE - offset;
        if (requested > sizeof(buffer)) {
            requested = sizeof(buffer);
        }

        size_t received = 0;
        while (received < requested) {
            ssize_t count = pread(file_fd, buffer + received, requested - received,
                                  (off_t)(offset + received));
            if (count < 0 && errno == EINTR) {
                continue;
            }
            if (count <= 0) {
                return PROBE_IO_FAILURE;
            }
            received += (size_t)count;
        }
        if (memcmp(buffer, payload + offset, requested) != 0) {
            return PROBE_CONTENT_MISMATCH;
        }
        offset += requested;
    }

    uint8_t extra;
    ssize_t extra_count;
    do {
        extra_count = pread(file_fd, &extra, 1, (off_t)PAYLOAD_SIZE);
    } while (extra_count < 0 && errno == EINTR);
    if (extra_count != 0) {
        return extra_count < 0 ? PROBE_IO_FAILURE : PROBE_CONTENT_MISMATCH;
    }

    struct stat after;
    if (fstat(file_fd, &after) != 0) {
        return PROBE_IO_FAILURE;
    }
    if (!same_inode(bound_file, &after) || before.st_size != after.st_size) {
        return PROBE_FILE_IDENTITY_CHANGED;
    }
    return PROBE_OK;
}

static int write_payload(int file_fd)
{
    size_t offset = 0;
    while (offset < PAYLOAD_SIZE) {
        ssize_t count = write(file_fd, payload + offset, PAYLOAD_SIZE - offset);
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count <= 0) {
            return -1;
        }
        offset += (size_t)count;
    }
    return fsync(file_fd);
}

static uid_t alternate_uid(void)
{
    return geteuid() == 0 ? (uid_t)65534 : (uid_t)(geteuid() + 1);
}

static int inject_after_parent_open(int parent_fd, MutationContext *context)
{
    int result = 0;
    switch (context->mutation) {
    case MUTATE_PARENT_SUBSTITUTE:
        if (rename(context->parent_path, context->moved_parent_path) != 0 ||
            mkdir(context->parent_path, 0700) != 0) {
            result = -1;
        }
        break;
    case MUTATE_PARENT_MODE:
        result = fchmod(parent_fd, 0755);
        break;
    case MUTATE_PARENT_OWNER:
        result = fchown(parent_fd, alternate_uid(), (gid_t)-1);
        break;
    default:
        break;
    }
    if (result != 0) {
        context->mutation_errno = errno;
        return -1;
    }
    if (context->mutation == MUTATE_PARENT_SUBSTITUTE ||
        context->mutation == MUTATE_PARENT_MODE ||
        context->mutation == MUTATE_PARENT_OWNER) {
        context->applied = true;
    }
    return 0;
}

static int inject_after_root_open(int root_fd, MutationContext *context)
{
    if (context->mutation == MUTATE_FILE_COLLISION) {
        int occupied_fd = openat(root_fd, SLOT_NAME,
                                 O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW,
                                 0600);
        if (occupied_fd < 0) {
            context->mutation_errno = errno;
            return -1;
        }
        context->applied = true;
        return close(occupied_fd);
    }
    if (context->mutation != MUTATE_FILE_SYMLINK) {
        return 0;
    }
    if (symlinkat("neighbor-target", root_fd, SLOT_NAME) != 0) {
        context->mutation_errno = errno;
        return -1;
    }
    context->applied = true;
    return 0;
}

static int inject_before_publish(int parent_fd, int root_fd, int file_fd,
                                 MutationContext *context)
{
    int result = 0;
    switch (context->mutation) {
    case MUTATE_ROOT_MODE:
        result = fchmod(root_fd, 0755);
        break;
    case MUTATE_ROOT_OWNER:
        result = fchown(root_fd, alternate_uid(), (gid_t)-1);
        break;
    case MUTATE_FILE_MODE:
        result = fchmod(file_fd, 0644);
        break;
    case MUTATE_FILE_OWNER:
        result = fchown(file_fd, alternate_uid(), (gid_t)-1);
        break;
    case MUTATE_FILE_HARDLINK:
        result = linkat(root_fd, SLOT_NAME, root_fd, HARDLINK_NAME, 0);
        break;
    default:
        break;
    }
    if (result != 0) {
        context->mutation_errno = errno;
        return -1;
    }
    if (context->mutation == MUTATE_ROOT_MODE ||
        context->mutation == MUTATE_ROOT_OWNER ||
        context->mutation == MUTATE_FILE_MODE ||
        context->mutation == MUTATE_FILE_OWNER ||
        context->mutation == MUTATE_FILE_HARDLINK) {
        context->applied = true;
    }
    (void)parent_fd;
    return 0;
}

static ProbeStatus allocate_and_prove(int slash_fd, const char *parent_path,
                                      const char *root_name, const char *file_name,
                                      MutationContext *context)
{
    const char *parent_relative = relative_absolute_path(parent_path);
    if (parent_relative == NULL || *parent_relative == '\0') {
        return PROBE_IO_FAILURE;
    }

    int parent_fd = openat2_call(slash_fd, parent_relative,
                                 O_RDONLY | O_DIRECTORY | O_CLOEXEC, 0,
                                 RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS);
    if (parent_fd < 0) {
        return errno == ELOOP ? PROBE_PARENT_SYMLINK : PROBE_IO_FAILURE;
    }

    struct stat bound_parent;
    if (fstat(parent_fd, &bound_parent) != 0) {
        close(parent_fd);
        return PROBE_IO_FAILURE;
    }
    ProbeStatus result = parent_policy_status(&bound_parent);
    if (result != PROBE_OK) {
        close(parent_fd);
        return result;
    }

    if (inject_after_parent_open(parent_fd, context) != 0) {
        close(parent_fd);
        return PROBE_HARNESS_FAILURE;
    }
    result = verify_parent(slash_fd, parent_path, parent_fd, &bound_parent);
    if (result != PROBE_OK) {
        close(parent_fd);
        return result;
    }

    if (mkdirat(parent_fd, root_name, 0700) != 0) {
        result = errno == EEXIST ? classify_root_collision(parent_fd, root_name)
                                 : PROBE_IO_FAILURE;
        close(parent_fd);
        return result;
    }

    int root_fd = openat2_call(parent_fd, root_name,
                               O_RDONLY | O_DIRECTORY | O_CLOEXEC, 0,
                               RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS |
                                   RESOLVE_NO_XDEV);
    if (root_fd < 0) {
        close(parent_fd);
        return errno == ELOOP ? PROBE_ROOT_SYMLINK : PROBE_IO_FAILURE;
    }

    struct stat bound_root;
    if (fstat(root_fd, &bound_root) != 0) {
        close(root_fd);
        close(parent_fd);
        return PROBE_IO_FAILURE;
    }
    if (fchmod(root_fd, 0700) != 0 || fstat(root_fd, &bound_root) != 0) {
        close(root_fd);
        close(parent_fd);
        return PROBE_IO_FAILURE;
    }
    result = verify_root(parent_fd, root_fd, root_name, &bound_root);
    if (result != PROBE_OK) {
        close(root_fd);
        close(parent_fd);
        return result;
    }

    if (inject_after_root_open(root_fd, context) != 0) {
        close(root_fd);
        close(parent_fd);
        return PROBE_HARNESS_FAILURE;
    }

    int file_fd = openat2_call(root_fd, file_name,
                               O_RDWR | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW,
                               0600,
                               RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS |
                                   RESOLVE_NO_XDEV);
    if (file_fd < 0) {
        result = (errno == EEXIST || errno == ELOOP)
                     ? classify_file_collision(root_fd, file_name)
                     : PROBE_IO_FAILURE;
        close(root_fd);
        close(parent_fd);
        return result;
    }

    struct stat bound_file;
    if (fstat(file_fd, &bound_file) != 0) {
        close(file_fd);
        close(root_fd);
        close(parent_fd);
        return PROBE_IO_FAILURE;
    }
    if (fchmod(file_fd, 0600) != 0 || fstat(file_fd, &bound_file) != 0) {
        close(file_fd);
        close(root_fd);
        close(parent_fd);
        return PROBE_IO_FAILURE;
    }
    result = verify_file(root_fd, file_fd, file_name, &bound_file);
    if (result == PROBE_OK && write_payload(file_fd) != 0) {
        result = PROBE_IO_FAILURE;
    }
    if (result == PROBE_OK) {
        result = stream_content_proof(file_fd, &bound_file);
    }

    if (result == PROBE_OK && inject_before_publish(parent_fd, root_fd, file_fd,
                                                    context) != 0) {
        result = PROBE_HARNESS_FAILURE;
    }
    if (result == PROBE_OK) {
        result = verify_parent(slash_fd, parent_path, parent_fd, &bound_parent);
    }
    if (result == PROBE_OK) {
        result = verify_root(parent_fd, root_fd, root_name, &bound_root);
    }
    if (result == PROBE_OK) {
        result = verify_file(root_fd, file_fd, file_name, &bound_file);
    }

    close(file_fd);
    close(root_fd);
    close(parent_fd);
    return result;
}

static int make_directory_at(int parent_fd, const char *name)
{
    if (mkdirat(parent_fd, name, 0700) != 0) {
        return -1;
    }
    int fd = openat(parent_fd, name, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) {
        return -1;
    }
    int result = fchmod(fd, 0700);
    int saved_errno = errno;
    close(fd);
    errno = saved_errno;
    return result;
}

static bool make_path(char *destination, size_t capacity,
                      const char *base, const char *case_name,
                      const char *leaf)
{
    int length = snprintf(destination, capacity, "%s/%s/%s", base, case_name, leaf);
    return length >= 0 && (size_t)length < capacity;
}

static bool add_neighbor(int parent_fd, NeighborRecord *record)
{
    static const char contents[] = "neighboring allowed input\n";
    int fd = openat(parent_fd, "neighbor.dat",
                    O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (fd < 0) {
        return false;
    }
    size_t offset = 0;
    while (offset < sizeof(contents) - 1) {
        ssize_t count = write(fd, contents + offset, sizeof(contents) - 1 - offset);
        if (count < 0 && errno == EINTR) {
            continue;
        }
        if (count <= 0) {
            close(fd);
            return false;
        }
        offset += (size_t)count;
    }
    if (fsync(fd) != 0 || fstat(fd, &record->identity) != 0) {
        close(fd);
        return false;
    }
    record->contents = contents;
    record->length = sizeof(contents) - 1;
    return close(fd) == 0;
}

static bool verify_neighbor(int slash_fd, const char *parent_path,
                            const NeighborRecord *record)
{
    const char *relative_path = relative_absolute_path(parent_path);
    if (relative_path == NULL) {
        return false;
    }
    int parent_fd = openat2_call(slash_fd, relative_path,
                                 O_RDONLY | O_DIRECTORY | O_CLOEXEC, 0,
                                 RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS);
    if (parent_fd < 0) {
        return false;
    }
    int file_fd = openat2_call(parent_fd, "neighbor.dat", O_RDONLY | O_CLOEXEC, 0,
                               RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS |
                                   RESOLVE_NO_XDEV);
    close(parent_fd);
    if (file_fd < 0) {
        return false;
    }

    struct stat current;
    bool valid = fstat(file_fd, &current) == 0 &&
                 same_inode(&record->identity, &current) &&
                 current.st_size == (off_t)record->length;
    char buffer[128];
    if (record->length > sizeof(buffer)) {
        valid = false;
    }
    if (valid) {
        ssize_t count;
        do {
            count = pread(file_fd, buffer, record->length, 0);
        } while (count < 0 && errno == EINTR);
        valid = count == (ssize_t)record->length &&
                memcmp(buffer, record->contents, record->length) == 0;
    }
    close(file_fd);
    return valid;
}

static bool setup_scenario(int base_fd, const char *base_path,
                           const Scenario *scenario, MutationContext *context,
                           int *parent_setup_fd, NeighborRecord *neighbor)
{
    if (mkdirat(base_fd, scenario->name, 0700) != 0) {
        return false;
    }
    int case_fd = openat(base_fd, scenario->name,
                         O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    if (case_fd < 0) {
        return false;
    }

    if (scenario->parent_is_symlink) {
        if (make_directory_at(case_fd, "real-parent") != 0 ||
            symlinkat("real-parent", case_fd, "parent") != 0) {
            close(case_fd);
            return false;
        }
    } else if (make_directory_at(case_fd, "parent") != 0) {
        close(case_fd);
        return false;
    }

    if (!make_path(context->parent_path, sizeof(context->parent_path),
                   base_path, scenario->name, "parent") ||
        !make_path(context->moved_parent_path, sizeof(context->moved_parent_path),
                   base_path, scenario->name, "moved-parent")) {
        close(case_fd);
        return false;
    }
    context->mutation = scenario->mutation;
    context->mutation_errno = 0;
    context->applied = false;

    int parent_fd = -1;
    if (!scenario->parent_is_symlink) {
        parent_fd = openat(case_fd, "parent",
                           O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
        if (parent_fd < 0) {
            close(case_fd);
            return false;
        }
    }

    if (scenario->occupied_root_directory &&
        make_directory_at(parent_fd, RUN_ROOT_NAME) != 0) {
        close(parent_fd);
        close(case_fd);
        return false;
    }
    if (scenario->symlink_root &&
        symlinkat("root-target", parent_fd, RUN_ROOT_NAME) != 0) {
        close(parent_fd);
        close(case_fd);
        return false;
    }
    if (scenario->add_neighbor && !add_neighbor(parent_fd, neighbor)) {
        close(parent_fd);
        close(case_fd);
        return false;
    }

    *parent_setup_fd = parent_fd;
    close(case_fd);
    return true;
}

static bool run_scenario(int slash_fd, int base_fd, const char *base_path,
                         const Scenario *scenario, ScenarioResult *scenario_result)
{
    MutationContext context = {0};
    NeighborRecord neighbor = {0};
    int setup_parent_fd = -1;
    scenario_result->observed = false;
    scenario_result->passed = false;
    scenario_result->actual = PROBE_HARNESS_FAILURE;
    if (!setup_scenario(base_fd, base_path, scenario, &context,
                        &setup_parent_fd, &neighbor)) {
        fprintf(stderr, "FAIL %s: scenario setup failed: %s\n",
                scenario->name, strerror(errno));
        return false;
    }

    ProbeStatus actual = allocate_and_prove(slash_fd, context.parent_path,
                                            RUN_ROOT_NAME, SLOT_NAME, &context);
    scenario_result->observed = true;
    scenario_result->actual = actual;
    if (setup_parent_fd >= 0) {
        close(setup_parent_fd);
    }

    bool passed = actual == scenario->expected;
    if (!passed) {
        fprintf(stderr, "FAIL %s: expected=%s actual=%s\n", scenario->name,
                status_name(scenario->expected), status_name(actual));
    } else {
        fprintf(stderr, "PASS %s: expected=%s actual=%s\n", scenario->name,
                status_name(scenario->expected), status_name(actual));
    }

    if (scenario->mutation != MUTATE_NONE && !context.applied) {
        fprintf(stderr, "FAIL %s: mutation hook did not run\n", scenario->name);
        passed = false;
    }
    if (actual == PROBE_HARNESS_FAILURE) {
        fprintf(stderr, "FAIL %s: injected operation failed: %s\n", scenario->name,
                strerror(context.mutation_errno));
        passed = false;
    }
    if (scenario->add_neighbor &&
        !verify_neighbor(slash_fd, context.parent_path, &neighbor)) {
        fprintf(stderr, "FAIL %s: neighboring allowed file changed or became unavailable\n",
                scenario->name);
        passed = false;
    }
    scenario_result->passed = passed;
    return passed;
}

static int remove_tree_entry(const char *path, const struct stat *info,
                             int type, struct FTW *walk)
{
    (void)info;
    (void)type;
    (void)walk;
    return remove(path);
}

static void emit_report(const Scenario *scenarios, const ScenarioResult *results,
                        size_t total, size_t observed, const char *status,
                        const char *diagnostic)
{
    printf("{\"schema\":\"issue779-linux-allocation-proof-v1\","
           "\"admission\":\"none\",\"consumer\":{"
           "\"name\":\"AppSurface GitHub Actions\",\"ciOwner\":\"Andrew\","
           "\"acceptance\":\"unverified\"},\"status\":\"%s\","
           "\"requiredProbeCount\":%zu,\"observedProbeCount\":%zu,"
           "\"contentProof\":{\"method\":\"bounded-byte-equality\","
           "\"bytes\":%u,\"chunkBytes\":%u,\"cryptographicHash\":false},"
           "\"scenarios\":[",
           status, total, observed, PAYLOAD_SIZE, STREAM_CHUNK);
    for (size_t i = 0; i < total; i++) {
        printf("%s{\"name\":\"%s\",\"expected\":\"%s\","
               "\"observed\":%s,\"actual\":\"%s\",\"passed\":%s}",
               i == 0 ? "" : ",", scenarios[i].name,
               status_name(scenarios[i].expected),
               results[i].observed ? "true" : "false",
               status_name(results[i].actual),
               results[i].passed ? "true" : "false");
    }
    printf("],\"diagnostic\":%s}\n", diagnostic == NULL ? "null" :
           (strcmp(diagnostic, "openat2-resolution-flags-unsupported") == 0
                ? "\"openat2-resolution-flags-unsupported\""
                : "\"fixture-setup-or-probe-failed\""));
}

int main(void)
{
    static const Scenario scenarios[] = {
        {"fresh-root-and-neighbor", PROBE_OK, MUTATE_NONE, false, false, false, true},
        {"occupied-root-collision", PROBE_ROOT_COLLISION, MUTATE_NONE, false, true, false, false},
        {"occupied-file-collision", PROBE_FILE_COLLISION, MUTATE_FILE_COLLISION, false, false, false, false},
        {"symlink-parent", PROBE_PARENT_SYMLINK, MUTATE_NONE, true, false, false, false},
        {"symlink-final-root", PROBE_ROOT_SYMLINK, MUTATE_NONE, false, false, true, false},
        {"symlink-file", PROBE_FILE_SYMLINK, MUTATE_FILE_SYMLINK, false, false, false, false},
        {"hardlink-file-before-publish", PROBE_FILE_HARDLINK, MUTATE_FILE_HARDLINK, false, false, false, false},
        {"parent-substitution", PROBE_PARENT_IDENTITY_CHANGED, MUTATE_PARENT_SUBSTITUTE, false, false, false, false},
        {"parent-mode-change", PROBE_PARENT_MODE_CHANGED, MUTATE_PARENT_MODE, false, false, false, false},
        {"parent-owner-change", PROBE_PARENT_OWNER_CHANGED, MUTATE_PARENT_OWNER, false, false, false, false},
        {"root-mode-change", PROBE_ROOT_MODE_CHANGED, MUTATE_ROOT_MODE, false, false, false, false},
        {"root-owner-change", PROBE_ROOT_OWNER_CHANGED, MUTATE_ROOT_OWNER, false, false, false, false},
        {"file-mode-change", PROBE_FILE_MODE_CHANGED, MUTATE_FILE_MODE, false, false, false, false},
        {"file-owner-change", PROBE_FILE_OWNER_CHANGED, MUTATE_FILE_OWNER, false, false, false, false}
    };
    const size_t total = sizeof(scenarios) / sizeof(scenarios[0]);
    ScenarioResult results[sizeof(scenarios) / sizeof(scenarios[0])] = {0};
    int support_status = openat2_is_supported();
    if (support_status != 0) {
        emit_report(scenarios, results, total, 0,
                    support_status == 77 ? "unsupported" : "failed",
                    support_status == 77 ? "openat2-resolution-flags-unsupported"
                                         : "openat2-feature-probe-failed");
        return support_status == 77 ? 77 : 1;
    }

    for (size_t i = 0; i < PAYLOAD_SIZE; i++) {
        payload[i] = (uint8_t)((i * 131U + 17U) & 0xffU);
    }

    char temporary_root[] = "/tmp/evidencehost-allocation-XXXXXX";
    if (mkdtemp(temporary_root) == NULL) {
        fprintf(stderr, "ERROR: mkdtemp failed: %s\n", strerror(errno));
        emit_report(scenarios, results, total, 0, "failed",
                    "fixture-setup-or-probe-failed");
        return 1;
    }
    int base_fd = open(temporary_root,
                       O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    int slash_fd = open("/", O_PATH | O_DIRECTORY | O_CLOEXEC);
    if (base_fd < 0 || slash_fd < 0) {
        fprintf(stderr, "ERROR: could not retain fixture root handles: %s\n",
                strerror(errno));
        if (base_fd >= 0) close(base_fd);
        if (slash_fd >= 0) close(slash_fd);
        (void)nftw(temporary_root, remove_tree_entry, 16, FTW_DEPTH | FTW_PHYS);
        emit_report(scenarios, results, total, 0, "failed",
                    "fixture-setup-or-probe-failed");
        return 1;
    }

    size_t passed = 0;
    size_t observed = 0;
    for (size_t i = 0; i < sizeof(scenarios) / sizeof(scenarios[0]); i++) {
        if (run_scenario(slash_fd, base_fd, temporary_root, &scenarios[i],
                         &results[i])) {
            passed++;
        }
        if (results[i].observed) {
            observed++;
        }
    }

    close(slash_fd);
    close(base_fd);
    if (nftw(temporary_root, remove_tree_entry, 16, FTW_DEPTH | FTW_PHYS) != 0) {
        fprintf(stderr, "ERROR: fixture temporary tree cleanup failed: %s\n",
                strerror(errno));
        emit_report(scenarios, results, total, observed, "failed",
                    "fixture-setup-or-probe-failed");
        return 1;
    }

    if (passed != total || observed != total) {
        fprintf(stderr, "FAIL: %zu/%zu scenarios passed; this fixture is not acceptance evidence\n",
                passed, total);
        emit_report(scenarios, results, total, observed, "failed",
                    "fixture-setup-or-probe-failed");
        return 1;
    }
    emit_report(scenarios, results, total, observed, "passed-mechanism-only", NULL);
    return 0;
}
