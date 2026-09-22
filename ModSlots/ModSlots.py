#! /usr/bin/env python3

import sys
sys.dont_write_bytecode = True
import os
import os.path
import shutil
import re
import struct
import time
import random
from enum import Enum
from hashlib import sha256
from zipfile import ZipFile

try:
    import unrardll
    RAR_SUPPORT = True
except ImportError:
    RAR_SUPPORT = False

try:
    from py7zr import SevenZipFile
    SEVEN_ZIP_SUPPORT = True
except ImportError:
    SEVEN_ZIP_SUPPORT = False

from ArmorIDs import ArmorIDs

def flatten(xss):
    return [x for xs in xss for x in xs]

NATIVEPC_SUBDIRS = ('ec', 'hm', 'npc', 'pg', 'posteffect', 'sound', 'ui', 'village', 'accessory', 'em', 'light', 'otomo', 'photo', 'quest', 'system', 'unit_resource', 'common', 'event', 'ngword_list', 'pc', 'pl', 'shortcut', 'vfx', 'wp', 'plugins')

USAGE = f'Usage: {sys.argv[0]} <Write/Verify/Delete> <Slots.txt> <Path_to_Mods> <Path_to_Game>'

if len(sys.argv) < 4:
    print(USAGE)
    sys.exit(1)

Mode = sys.argv[1].lower()
Slots = sys.argv[2]
Mods_Directory = sys.argv[3]
Game_Directory = sys.argv[4]
Extract_Directory = f'{Mods_Directory}/extract'
List_Path = f'{Game_Directory}/mod_slots.txt'

if not os.path.isfile(Slots):
    print(f'{Slots} not a file.')
    sys.exit(1)

if not os.path.isdir(Mods_Directory):
    print(f'Directory {Mods_Directory} doesn\'t exist.')
    sys.exit(1)

if not os.path.isdir(Extract_Directory):
    try:
        os.mkdir(Extract_Directory)
    except:
        print(f'Failed to create directory for extracted mods at {Extract_Directory}.')
        sys.exit(1)

if not os.path.isdir(Game_Directory):
    print(f'Directory {Game_Directory} doesn\'t exist.')
    sys.exit(1)

if Mode == 'verify' and not os.path.isfile(List_Path):
    print(f'Verification requested but previous hash not found at {List_Path}.')
    sys.exit(1)

Error_Messages = []
Error_Index = 1
def Error(msg):
    global Error_Messages
    global Error_Index
    msg = f'E(#{Error_Index}): {msg}'
    Error_Messages.append(msg)
    Error_Index += 1
    print(msg + '\n' + '^'*len(msg))

Warning_Index = 1
def Warn(msg, inline=True):
    global Error_Messages
    global Warning_Index
    msg = f'W(#{Warning_Index}): {msg}'
    Error_Messages.append(msg)
    Warning_Index += 1
    if inline:
        print(msg + '\n' + '^'*len(msg))

class ArchiveType(Enum):
    ZIP = 0
    RAR = 1
    SEVEN_ZIP = 2

class Mod():
    def __init__(self, path):
        self.path = path
        self.name = path.split('/')[-1]
        m = sha256()
        m.update(open(path, 'rb').read())
        self.hash = m.hexdigest()
        ext = path.split('.')[-1].lower()
        self.files = []
        if ext == 'zip':
            self.type = ArchiveType.ZIP
            self.obj = ZipFile(path, 'r')
            for file in self.obj.infolist():
                if file.filename[-1] == '/':
                    continue
                self.files.append(file.filename)
        elif ext == 'rar':
            if not RAR_SUPPORT:
                raise Exception('Use of .rar archive without required package (unrardll).')
            self.type = ArchiveType.RAR
            for header in list(unrardll.headers(path)):
                if not header['is_dir']:
                    self.files.append(header['filename'])
        elif ext == '7z':
            if not SEVEN_ZIP_SUPPORT:
                raise Exception('Use of .7z archive without required package (py7zr).')
            self.type = ArchiveType.SEVEN_ZIP
            self.obj = SevenZipFile(path, 'r')
            for file in self.obj.list():
                if not file.is_directory:
                    self.files.append(file.filename)
        # Filter paths to fix automatic processing.
        self.raw_files = self.files.copy()
        for i, file in enumerate(self.files):
            npc = file.lower().find('nativepc')
            if npc >= 0:
                cut = file[npc:npc + len('nativePC')]
                if cut != 'nativePC':
                    print(f'Correcting nativePC capitalization in {file}.')
                    self.files[i] = file.replace(cut, 'nativePC')
        self.raw_map = {}
        for i in range(0, len(self.files)):
            self.raw_map[self.files[i]] = self.raw_files[i]
        self.roots = []
        self.ignored = []
        self.overrides = {}
        self.map = {}

    def sub_override_name(self, name, is_slinger):
        if name[0] == '[':
            name = name[1:-1]
            if name in ArmorIDs.keys():
                name = ArmorIDs[name][1 if is_slinger else 0][2:]
            else:
                return None
        return name

    def get_id_from_subpath(self, sub):
        if sub.startswith('nativePC/pl/f_equip/pl'):
            return sub[22:30], False
        elif sub.startswith('nativePC/wp/slg/slg'):
            return sub[18:27], True
        return None, None

    def file_sort_key(self, x):
        for i in range(0, len(self.roots)):
            if x.startswith(self.roots[i]):
                return i
        return 0

    # If file is within a defined root, truncate it's path up to nativePC/.
    def maybe_strip_root(self, file):
        for root in self.roots:
            if file.startswith(root):
                npc = file.find('nativePC')
                if npc >= 0:
                    return file[npc:], root
        return file, None

    def do_map(self, All_Files, Global_Overrides):
        print(self.name)
        for file in sorted(self.files, key=self.file_sort_key):
            base, root = self.maybe_strip_root(file)
            has_overrides = False
            for key in self.overrides.keys():
                if base.startswith(key):
                    has_overrides = True
                    break
            if (base.startswith(tuple(self.ignored)) or not base.startswith('nativePC')) and not has_overrides:
                continue
            has_global_override = False
            for key in Global_Overrides.values():
                if base.startswith(key[1]):
                    has_global_override = True
                    break
            targets = []
            if base in self.overrides.keys() and len(self.overrides[base]) > 0: # Direct remaps.
                for target in self.overrides[base]:
                    if len(target.split('/')[-1].split('.')) == 1:
                        print(f'Warning: Possible non-direct path as a direct override: {target}')
                    targets.append(target)
            # Add file as-is unless it could override an explicit target of a different file.
            elif base not in flatten(self.overrides.values()) and not has_global_override:
                targets.append(base)
            # Substitute armor names, like '[Anja]', with their IDs.
            for i, target in enumerate(targets):
                for key, overrides in self.overrides.items():
                    if not target.startswith(key) or key == target:
                        continue
                    for override in overrides:
                        target_id, is_slinger = self.get_id_from_subpath(target)
                        if not target_id:
                            Error(f'Invalid target: {target}')
                            continue
                        mapped_name = self.sub_override_name(override, is_slinger)
                        if not mapped_name:
                            Error(f'Invalid substitute: {override}')
                            continue
                        targets[i] = target.replace(target_id, mapped_name)
                    break
            print(f' {file}')
            # Apply global overrides.
            for match, override in Global_Overrides.items():
                for target in targets:
                    if re.match(match, target):
                        override[0].map[override[1]].append(target)
                        targets.remove(target)
                        print(f'  (Globally overwritten by) {override[0].name}:{override[1]}')
                        print(f'   -> {target}')
                        All_Files.append((self, target, file))
                        break
            # Map targets.
            unique_targets = []
            for target in targets:
                overwritten = None
                for other in All_Files:
                    if other[1] == target:
                        overwritten = other
                        break
                if overwritten:
                    if overwritten[0] == self:
                        print(f'  (Overwritten by) {overwritten[2]}')
                    else:
                        print(f'  (Overwritten by) {overwritten[0].name}:{overwritten[2]}')
                else:
                    unique_targets.append(target)
                    print(f'  -> {target}')
            if len(unique_targets) > 0:
                for target in unique_targets:
                    All_Files.append((self, target, file))
                self.map[file] = unique_targets

    def add_to_hash(self, m):
        output = f'{os.path.join(Extract_Directory, self.hash)}'
        if not os.path.isdir(output):
            os.mkdir(output)
        do_extract = False
        on_disk = [os.path.join(root, file) for root, dirs, files in os.walk(output) for file in files]
        for key in self.map:
            file = os.path.join(output, self.raw_map[key])
            if file not in on_disk:
                do_extract = True
                break
        if do_extract:
            if self.type == ArchiveType.ZIP:
                self.obj.extractall(path=output)
            elif self.type == ArchiveType.RAR:
                unrardll.extract(self.path, output)
            elif self.type == ArchiveType.SEVEN_ZIP:
                self.obj.extractall(path=output)
        for key in self.map:
            m.update(open(os.path.join(output, self.raw_map[key]), 'rb').read())

    def write(self, old_list, list_file):
        output = f'{os.path.join(Extract_Directory, self.hash)}'
        for key in self.map:
            targets = self.map[key]
            for target in targets:
                copy = target[0] == '|';
                if copy:
                    target = target[1:]
                if target in old_list:
                    old_list.remove(target)
                list_file.write(target + '\n')
                inc = Game_Directory
                for s in target.split('/')[:-1]:
                    inc = os.path.join(inc, s)
                    if not os.path.isdir(inc):
                        os.mkdir(inc)
                target = os.path.join(Game_Directory, target)
                if os.path.islink(target):
                    os.remove(target)
                elif os.path.isfile(target) or os.path.isdir(target):
                    if not copy:
                        Warn(f'Not replacing non-symlink file: {target}', False)
                    continue
                file = os.path.join(output, self.raw_map[key])
                if copy:
                    print(f'{file}\n (detached) -> {target}')
                    shutil.copyfile(file, target)
                else:
                    os.symlink(file, target)

    def delete(self):
        for key in self.map:
            targets = self.map[key]
            for target in targets:
                target = os.path.join(Game_Directory, target)
                if os.path.islink(target):
                    os.remove(target)
                elif os.path.isfile(target) or os.path.isdir(target):
                    Warn(f'Not deleting non-symlink file: {target}', False)

def is_armor_override(line):
    sp = line.split('/')
    if len(sp[-1]) == 10 and sp[-1].startswith('pl'):
        return True
    if len(sp[-1]) == 11 and sp[-1].startswith('slg'):
        return True
    if sp[-1] in ['helm', 'body', 'arm', 'wst', 'leg']:
        return True
    return False

Seed = time.time()
if Mode == 'write':
    random.seed(Seed)

with open(List_Path, 'a+') as l:
    l.seek(0)
    old_list = l.read().splitlines()
    if len(old_list) > 2:
        old_hash = old_list[0]
        try:
            old_seed = struct.unpack('!d', bytes.fromhex(old_list[1]))[0]
        except ValueError:
            Error(f'Invalid seed \'{old_list[1]}\' in {List_Path}.')
        else:
            if Mode == 'verify':
                random.seed(old_seed)
        old_list = old_list[2:]
    if Mode == 'write':
        l.truncate(0)

Mods = []
Shuffle = None
Current_Mod = None
Current_Override = None
Global_Overrides = {}
with open(Slots, 'r') as f:
    for ln, line in enumerate(f.read().splitlines()):
        if len(line) == 0 or line[0] == '#':
            continue
        if line == 'XXX':
            break
        if line[0] in [';', 's']:
            if Current_Mod:
                Mods.append(Current_Mod)
                Current_Mod = None
        if line[0] == 's':
            Shuffle = []
            path = os.path.join(Mods_Directory, line[1:])
            if os.path.isfile(path):
                Shuffle.append(path)
            else:
                Error(f'File not found: {path}')
            continue
        elif line[0] == '+':
            path = os.path.join(Mods_Directory, line[1:])
            if os.path.isfile(path):
                Shuffle.append(path)
            else:
                Error(f'File not found: {path}')
            continue
        elif line[0:4].upper() == 'ROLL':
            Current_Mod = Mod(random.choice(Shuffle))
            Current_Override = None
            Shuffle = None
            continue
        if line[0] == ';':
            path = os.path.join(Mods_Directory, line[1:])
            if os.path.isfile(path):
                Current_Mod = Mod(path)
                Current_Override = None
            else:
                Error(f'File not found: {path}')
            continue
        cmd = None
        if line[0] == '&':
            cmd = 'root'
        elif line[0] == '<':
            cmd = 'ignore'
        elif line[0] == '|':
            cmd = 'detach'
        elif line[0] == ':':
            cmd = 'override'
        elif line[0] == '>':
            cmd = 'value'
        elif line[0] == '*':
            cmd = 'global'
        if cmd:
            line = line[1:]
            if line.startswith(NATIVEPC_SUBDIRS):
                line = 'nativePC/' + line
        else:
            continue
        if cmd == 'root' and Current_Mod:
            Current_Mod.roots.append(line)
        elif cmd == 'ignore' and Current_Mod:
            Current_Mod.ignored.append(line)
        elif cmd == 'detach' and Current_Mod:
            if line not in Current_Mod.files:
                Error(f'File not found: {Current_Mod.name}:{line}')
            else:
                Current_Mod.ignored.append(line)
                Current_Mod.map[line] = [f'|{line}']
        elif cmd == 'override' and Current_Mod:
            if not (is_armor_override(line) or line in Current_Mod.files):
                Error(f'File not found: {Current_Mod.name}:{line}')
            else:
                Current_Override = line
                Current_Mod.overrides[Current_Override] = []
        elif cmd == 'global' and Current_Mod and Current_Override:
            Current_Mod.map[Current_Override] = []
            Global_Overrides[line] = (Current_Mod, Current_Override)
        elif cmd == 'value' and Current_Mod:
            if Current_Override:
                Current_Mod.overrides[Current_Override].append(line)
            elif line[0] == '[': # Special case guess for armor mods.
                include_slinger = line[-1] == 's'
                if include_slinger:
                    line = line[:-1]
                for file in Current_Mod.files:
                    base, root = Current_Mod.maybe_strip_root(file)
                    if base.startswith('nativePC/pl/f_equip/pl') or (base.startswith('nativePC/wp/slg/slg') and include_slinger):
                        override = '/'.join(base.split('/')[0:4])
                        Current_Mod.overrides[override] = [line]
                    elif base.startswith('nativePC/wp/slg/slg') and not include_slinger:
                        Current_Mod.ignored.append(base)
    if Current_Mod:
        Mods.append(Current_Mod)
        Current_Mod = None

All_Files = []
M = sha256()
for mod in Mods:
    mod.do_map(All_Files, Global_Overrides)
    mod.add_to_hash(M)

if Mode == 'write':
    with open(List_Path, 'a+') as l:
        l.write(M.hexdigest() + '\n')
        l.write(hex(struct.unpack('!Q', struct.pack('!d', Seed))[0])[2:] + '\n')
        for mod in Mods:
            mod.write(old_list, l)
    if len(old_list) > 0:
        print('------------Removed------------')
    for old in old_list:
        old_target = os.path.join(Game_Directory, old)
        if os.path.islink(old_target):
            print(f'<{old_target}')
            os.remove(old_target)

if Mode == 'verify':
    if old_hash == M.hexdigest():
        print('Verification passed!')
    else:
        print('Verification failed.')
elif Mode == 'delete':
    for mod in Mods:
        mod.delete()

if len(Error_Messages) > 0:
    print('----------Warnings/Errors----------')
    for err in Error_Messages:
        print(err)
