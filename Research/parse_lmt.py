#! /usr/bin/env python3

import sys
import os
import io
import math
import struct
import ctypes
from ctypes import c_char, c_int8, c_uint8, c_int16, c_uint16, c_int32, c_uint32, c_int64, c_uint64, c_float
from typing import Optional, Self

# This is entirely based on MHW-Free-HyperKinetics (https://github.com/AsteriskAmpersand/MHW-Free-HyperKinetics).

class Vector():
    x: float
    y: float
    z: float
    def __str__(self) -> str:
        return f'({self.x:8.5f}, {self.y:8.5f}, {self.z:8.5f})'

class Quaternion():
    x: float
    y: float
    z: float
    w: float
    def __str__(self) -> str:
        return f'({self.x:12.9f}, {self.y:12.9f}, {self.z:12.9f}, {self.w:12.9f})'

class Tuple():
    v: float
    w: float
    def __str__(self) -> str:
        return f'({self.v:8.5f}, {self.w:8.5f})'

class V_Type():
    X = 1
    Y = 2
    Z = 3

class C_Struct(ctypes.LittleEndianStructure):
    @classmethod
    def size(cls) -> int:
        return ctypes.sizeof(cls)

    # Default unpack()/pack() relies on ctypes behavior.
    @classmethod
    def unpack(cls, data) -> Self:
        return cls.from_buffer_copy(data)

    def pack(self) -> bytes:
        return bytes(self)

class C_Packed_Value(C_Struct):
    struct_format: str

    @classmethod
    def size(cls) -> int:
        return struct.calcsize(cls.struct_format)

    def normalize(self) -> None:
        pass

    def denormalize(self) -> None:
        pass

    def __str__(self):
        s = '('
        for field in self._fields_ :
            s += f'{getattr(self, field[0])}, '
        s = s[0:-2] + ')'
        return s

class C_Packed_Value_Int(C_Packed_Value):
    struct_format: str
    bits: int
    offset: int = 8
    mulmin: int = 7
    is_quaternion: bool = False
    is_tuple: bool = False
    v_is: Optional[V_Type] = None
    raw_value: bool = False
    _norm_factor: Optional[int] = None

    def __init__(self):
        self.n = self.n_type()

    @classmethod
    def norm_factor(cls) -> int:
        if cls._norm_factor is None:
            cls._norm_factor = (2**cls.bits) - 1 - cls.mulmin - cls.offset
        return cls._norm_factor

    @classmethod
    def normalize_int_value(cls, v: int) -> float:
        return (v - cls.offset) / cls.norm_factor()

    @classmethod
    def denormalize_int_value(cls, v: float) -> int:
        return round((v * cls.norm_factor()) + cls.offset)

    def normalize(self) -> None:
        if self.is_tuple:
            self.n.v = self.normalize_int_value(self.v)
        else:
            self.n.x = self.normalize_int_value(self.x)
            self.n.y = self.normalize_int_value(self.y)
            self.n.z = self.normalize_int_value(self.z)
        if self.is_quaternion:
            self.n.w = self.normalize_int_value(self.w)

    def denormalize(self) -> None:
        if self.is_tuple:
            self.v = self.denormalize_int_value(self.n.v)
        else:
            self.x = self.denormalize_int_value(self.n.x)
            self.y = self.denormalize_int_value(self.n.y)
            self.z = self.denormalize_int_value(self.n.z)
        if self.is_quaternion:
            self.w = self.denormalize_int_value(self.n.w)

class Vector_Float_Base(C_Packed_Value):
    struct_format: str = '<fff'
    raw_value: bool = True
    _pack_ = 1
    _fields_ = [
        ("x", c_float),
        ("y", c_float),
        ("z", c_float)
    ]

    @classmethod
    def unpack(cls, data) -> Self:
        r = cls()
        data = struct.unpack(cls.struct_format, data)
        r.x = data[0]
        r.y = data[1]
        r.z = data[2]
        return r

    def pack(self) -> None:
        return struct.pack(self.struct_format, self.x, self.y, self.z)

class Vector_Float(C_Packed_Value):
    struct_format: str = '<fffI'
    raw_value: bool = True
    _pack_ = 1
    _fields_ = [
        ("x", c_float),
        ("y", c_float),
        ("z", c_float),
        ("frame", c_uint32)
    ]

    @classmethod
    def unpack(cls, data) -> Self:
        r = cls()
        data = struct.unpack(cls.struct_format, data)
        r.x = data[0]
        r.y = data[1]
        r.z = data[2]
        r.frame = data[3]
        return r

    def pack(self) -> None:
        return struct.pack(self.struct_format, self.x, self.y, self.z, self.frame)

class Vector_Short(C_Packed_Value_Int):
    struct_format: str = '<HHHH'
    bits: int = 16
    n_type: type = Vector
    _pack_ = 1
    _fields_ = [
        ("x", c_uint16),
        ("y", c_uint16),
        ("z", c_uint16),
        ("frame", c_uint16)
    ]

    @classmethod
    def unpack(cls, data) -> Self:
        r = cls()
        data = struct.unpack(cls.struct_format, data)
        r.x = data[0]
        r.y = data[1]
        r.z = data[2]
        r.frame = data[3]
        return r

    def pack(self) -> None:
        return struct.pack(self.struct_format, self.x, self.y, self.z, self.frame)

class Vector_Byte(C_Packed_Value_Int):
    struct_format: str = '<BBBB'
    bits: int = 8
    n_type: type = Vector
    _pack_ = 1
    _fields_ = [
        ("x", c_uint8),
        ("y", c_uint8),
        ("z", c_uint8),
        ("frame", c_uint8)
    ]

    @classmethod
    def unpack(cls, data) -> Self:
        r = cls()
        data = struct.unpack(cls.struct_format, data)
        r.x = data[0]
        r.y = data[1]
        r.z = data[2]
        r.frame = data[3]
        return r

    def pack(self) -> None:
        return struct.pack(self.struct_format, self.x, self.y, self.z, self.frame)

class Quaternion_7Bit(C_Packed_Value_Int):
    struct_format: str = '<I'
    bits: int = 7
    is_quaternion: bool = True
    n_type: type = Quaternion
    _pack_ = 1
    _fields_ = [
        ("w", c_uint32, 7),
        ("z", c_uint32, 7),
        ("y", c_uint32, 7),
        ("x", c_uint32, 7),
        ("frame", c_uint32, 4)
    ]

    # ffffxxxxxxxyyyyyyyzzzzzzzwwwwwww
    def pack(self) -> None:
        return struct.pack(self.struct_format,
            (self.frame << 28 | self.x << 21 | self.y << 14 | self.z << 7 | self.w) & 0xFFFFFFFF)

    @classmethod
    def unpack(cls, data) -> Self:
        r = cls()
        data = struct.unpack(cls.struct_format, data)[0]
        r.w =      data        & 0x7F
        r.z =     (data >> 7)  & 0x7F
        r.y =     (data >> 14) & 0x7F
        r.x =     (data >> 21) & 0x7F
        r.frame = (data >> 28) & 0xF
        return r

class Quaternion_9Bit(C_Packed_Value_Int):
    struct_format: str = '<BBBBB'
    bits: int = 9
    is_quaternion: bool = True
    n_type: type = Quaternion
    _pack_ = 1
    _fields_ = [
        ("x", c_uint64, 9),
        ("y", c_uint64, 9),
        ("z", c_uint64, 9),
        ("w", c_uint64, 9),
        ("frame", c_uint64, 4)
    ]

    # xxxxxxxx|yyyyyyyx|zzzzzzyy|wwwwwzzz|ffffwwww
    def pack(self) -> None:
        return struct.pack(self.struct_format,
             (self.x       >> 1)                & 0xFF,
            ((self.y >> 2) << 1 | self.x & 0x1) & 0xFF,
            ((self.z >> 3) << 2 | self.y & 0x3) & 0xFF,
            ((self.w >> 4) << 3 | self.z & 0x7) & 0xFF,
             (self.frame   << 4 | self.w & 0xF) & 0xFF)

    @classmethod
    def unpack(cls, data) -> Self:
        r = cls()
        data = struct.unpack(cls.struct_format, data)
        r.x =     (data[0] << 1       | data[1] & 0x1) & 0x1FF
        r.y =    ((data[1] >> 1) << 2 | data[2] & 0x3) & 0x1FF
        r.z =    ((data[2] >> 2) << 3 | data[3] & 0x7) & 0x1FF
        r.w =    ((data[3] >> 3) << 4 | data[4] & 0xF) & 0x1FF
        r.frame = (data[4] >> 4) & 0xF
        return r

class Quaternion_11Bit(C_Packed_Value_Int):
    struct_format: str = '<HHH'
    bits: int = 11
    is_quaternion: bool = True
    n_type: type = Quaternion
    _pack_ = 1
    _fields_ = [
        ("x", c_uint64, 11),
        ("y", c_uint64, 11),
        ("z", c_uint64, 11),
        ("w", c_uint64, 11),
        ("frame", c_uint64, 4)
    ]

    # yyyyyxxxxxxxxxxx|zzzzzzzzzzyyyyyy|ffffwwwwwwwwwwwz
    def pack(self) -> None:
        return struct.pack(self.struct_format,
            ((self.y >> 6) << 11 | self.x)                     & 0xFFFF,
            ((self.z >> 1) << 6  | self.y & 0x3f)              & 0xFFFF,
             (self.frame   << 12 | self.w << 1 | self.z & 0x1) & 0xFFFF)

    @classmethod
    def unpack(cls, data) -> Self:
        r = cls()
        data = struct.unpack(cls.struct_format, data)
        r.x =      data[0]                                 & 0x7FF
        r.y =    ((data[0] >> 11) << 6 | (data[1] & 0x3F)) & 0x7FF
        r.z =    ((data[1] >> 6)  << 1 | (data[2] & 0x1))  & 0x7FF
        r.w =     (data[2] >> 1)                           & 0x7FF
        r.frame = (data[2] >> 12)                          & 0xF
        return r

class Quaternion_14Bit(C_Packed_Value_Int):
    struct_format: str = '<Q'
    bits: int = 14
    is_quaternion: bool = True
    raw_value: bool = True
    n_type: type = Quaternion
    _pack_ = 1
    _fields_ = [
        ("w", c_uint64, 14),
        ("z", c_uint64, 14),
        ("y", c_uint64, 14),
        ("x", c_uint64, 14),
        ("frame", c_uint64, 8)
    ]

    @classmethod
    def normalize_int_value(cls, v: int) -> float:
        if v & (2**(cls.bits-1)): # Is the high bit set.
            mask = (2**(cls.bits)-1)
            absval = ((v & mask) ^ mask) + 1
            return (absval/(2**(cls.bits-1))) * -2.0
        else:
            return (v / (2**(cls.bits-1) - 1)) * 2.0

    @classmethod
    def denormalize_int_value(cls, v: float) -> int:
        if v < 0:
            absval = math.floor((abs(v/2) * (2**(cls.bits-1)-1)))
            absval ^= (2**(cls.bits)-1)
            return absval
        else:
            return math.floor(((v/2) * (2**(cls.bits-1)-1)))

    # ffffffffxxxxxxxxxxxxxxyyyyyyyyyyyyyyzzzzzzzzzzzzzzwwwwwwwwwwwwww
    def pack(self) -> None:
        return struct.pack(self.struct_format,
            (self.frame << 56 | self.x << 42 | self.y << 28 | self.z << 14 | self.w) & 0xFFFFFFFFFFFFFFFF)

    @classmethod
    def unpack(cls, data) -> Self:
        r = cls()
        data = struct.unpack(cls.struct_format, data)[0]
        r.w =      data        & 0x3FFF
        r.z =     (data >> 14) & 0x3FFF
        r.y =     (data >> 28) & 0x3FFF
        r.x =     (data >> 42) & 0x3FFF
        r.frame = (data >> 56) & 0xFF
        return r

class Tuple_14Bit(C_Packed_Value_Int):
    struct_format: str = '<I'
    bits: int = 14
    is_quaternion: bool = True
    is_tuple: bool = True
    n_type: type = Tuple
    _pack_ = 1
    _fields_ = [
        ("v", c_uint32, 14),
        ("w", c_uint32, 14),
        ("frame", c_uint32, 4)
    ]

    # ffffwwwwwwwwwwwwwwvvvvvvvvvvvvvv
    def pack(self) -> None:
        return struct.pack(self.struct_format, (self.frame << 28 | self.w << 14 | self.v) & 0xFFFFFFFF)

    @classmethod
    def unpack(cls, data) -> Self:
        r = cls()
        data = struct.unpack(cls.struct_format, data)[0]
        r.v =      data & 0x3FFF
        r.w =     (data >> 14) & 0x3FFF
        r.frame = (data >> 28) & 0xF
        return r

class Tuple_XW_14Bit(Tuple_14Bit):
    v_is: V_Type = V_Type.X

class Tuple_YW_14Bit(Tuple_14Bit):
    v_is: V_Type = V_Type.Y

class Tuple_ZW_14Bit(Tuple_14Bit):
    v_is: V_Type = V_Type.Z

class LMT_Header(C_Struct):
    _pack_ = 1
    _fields_ = [
        ('signature', c_char * 4),
        ('version', c_int16),
        ('entry_count', c_int16),
        ('unknown', c_char * 8)
    ]

class LMT_Basis(C_Struct):
    _pack_ = 1
    _fields_ = [
        ('mul', c_float * 4),
        ('add', c_float * 4)
    ]

class LMT_Bone_Data(C_Struct):
    values: list[C_Packed_Value]
    lerp: Optional[LMT_Basis] = None
    _pack_ = 1
    _fields_ = [
        ('buffer_type', c_uint8),
        ('raw_usage', c_uint8),
        ('joint_type', c_uint8),
        ('two_zero_five', c_uint8),
        ('bone_id', c_int32),
        ('weight', c_float),
        ('buffer_size', c_int32),
        ('buffer_offset', c_int64),
        ('basis', c_float * 4),
        ('lerp_offset', c_int64)
    ]

    @property
    def usage(self):
        return self.raw_usage

class LMT_Action(C_Struct):
    action_id: int
    offset: int
    bone_headers: list[LMT_Bone_Data]
    bones: dict[int, dict[int, list[C_Packed_Value]]]
    free_lerp_offsets: list[int]
    _pack_ = 1
    _fields_ = [
        ('fcurve_offset', c_uint64),
        ('fcurve_count', c_uint32),
        ('frame_count', c_uint32),
        ('loop_count', c_int32),
        ('null0', c_int32 * 3),
        ('vec0', c_float * 4),
        ('vec2', c_float * 4),
        ('flags', c_uint8),
        ('null2', c_char * 2),
        ('flags2', c_uint8),
        ('null3', c_int32 * 5),
        ('timl_offset', c_uint64)
    ]

    def get_bone_header_by_id_and_usage(self, bone_id: int, usage: int) -> Optional[LMT_Bone_Data]:
        for data in self.bone_headers:
            if data.bone_id == bone_id and data.usage == usage:
                return data
        return None

    def normalize_keyframes(self) -> None:
        for data in self.bone_headers:
            for value in data.values:
                value.normalize()

    def denormalize_keyframes(self) -> None:
        for data in self.bone_headers:
            for value in data.values:
                value.denormalize()

    def write(self) -> None:
        seek(self.offset)
        write(self.pack())
        seek(self.fcurve_offset)
        for data in self.bone_headers:
            write(data.pack())
        for data in self.bone_headers:
            if data.buffer_offset != 0:
                seek(data.buffer_offset)
                for value in data.values:
                    write(value.pack())
            if data.lerp_offset != 0:
                seek(data.lerp_offset)
                write(data.lerp.pack())

if len(sys.argv) < 3:
    print('Usage: ./parse_lmt.py <input.lmt> <output.lmt>')
    sys.exit(1)

LMT_File: io.BytesIO = io.BytesIO(open(sys.argv[1], 'rb').read())
def read(n: int) -> bytes:
    global LMT_File
    return LMT_File.read(n)
def write(value: bytes):
    global LMT_File
    return LMT_File.write(value)
def seek(offset: int) -> None:
    global LMT_File
    LMT_File.seek(offset)

header = LMT_Header.unpack(read(16))
offsets = struct.unpack(f'<{header.entry_count}Q', read(8 * header.entry_count))
padding = read(16)

action_headers = []
for i, offset in enumerate(offsets):
    if offset != 0:
        seek(offset)
        action_header = LMT_Action.unpack(read(96))
        action_header.action_id = i
        action_header.offset = offset
        action_header.bones = {}
        action_header.free_lerp_offsets = []
        action_headers.append(action_header)

buffer_types = {
    1: Vector_Float_Base,
    2: Vector_Float_Base,
    3: Vector_Float,
    4: Vector_Short,
    5: Vector_Byte,
    6: Quaternion_14Bit,
    7: Quaternion_7Bit,
    11: Tuple_XW_14Bit,
    12: Tuple_YW_14Bit,
    13: Tuple_ZW_14Bit,
    14: Quaternion_11Bit,
    15: Quaternion_9Bit
}

for action in action_headers:
    seek(action.fcurve_offset)
    action.bone_headers = []
    for _ in range(action.fcurve_count):
        data = LMT_Bone_Data.unpack(read(48))
        data.values = []
        if data.bone_id not in action.bones:
            action.bones[data.bone_id] = {}
        if data.usage not in action.bones[data.bone_id]:
            action.bones[data.bone_id][data.usage] = []
        action.bone_headers.append(data)
    for data in action.bone_headers:
        if data.buffer_offset != 0:
            seek(data.buffer_offset)
            packed_type = buffer_types[data.buffer_type]
            for _ in range(data.buffer_size // packed_type.size()):
                value = packed_type.unpack(read(packed_type.size()))
                data.values.append(value)
                action.bones[data.bone_id][data.usage].append(data.values[-1])
        if data.lerp_offset != 0:
            seek(data.lerp_offset)
            data.lerp = LMT_Basis.unpack(read(32))

def get_action_by_id(action_headers: list[LMT_Action], action_id: int) -> Optional[LMT_Action]:
    for action in action_headers:
        if action.action_id == action_id:
            return action
    return None

def print_basis_report(action_headers: list[LMT_Action]) -> None:
    bone_to_basis_values = {}
    for action in action_headers:
        for data in action.bone_headers:
            if data.usage != 1:
                continue
            if data.bone_id not in bone_to_basis_values:
                bone_to_basis_values[data.bone_id] = {}
            if action.action_id not in bone_to_basis_values[data.bone_id]:
                bone_to_basis_values[data.bone_id][action.action_id] = []
            bone_to_basis_values[data.bone_id][action.action_id].append(
                [data.basis, data.lerp.mul if data.lerp else [0, 0, 0, 0], data.lerp.add if data.lerp else [0, 0, 0, 0]]
            )
    for bone_id in bone_to_basis_values.keys():
        print(f'Bone#{bone_id}')
        for action_id in sorted(bone_to_basis_values[bone_id].keys()):
            for values in bone_to_basis_values[bone_id][action_id]:
                basis = values[0]
                mul = values[1]
                add = values[2]
                print(f' [{action_id}] ({basis[0]:6.3f}, {basis[1]:6.3f}, {basis[2]:6.3f}, {basis[3]:6.3f}) ({mul[0]:6.3f}, {mul[1]:6.3f}, {mul[2]:6.3f}, {mul[3]:6.3f}) ({add[0]:6.3f}, {add[1]:6.3f}, {add[2]:6.3f}, {add[3]:6.3f})')

def print_keyframes(action: LMT_Action, bone_id: int) -> None:
    for data in action.bone_headers:
        if data.bone_id == -1:
            print(*action.vec2, *action.vec0)
        if data.bone_id != bone_id:
            continue
        print('Usage: ', data.usage)
        print('Basis: ', *data.basis)
        if data.lerp:
            print('Add: ', *data.lerp.add)
            print('Mul: ', *data.lerp.mul)
        for value in data.values:
            if not value.raw_value:
                if value.is_tuple:
                    value.n.v = value.n.v * data.lerp.mul[0] + data.lerp.add[0]
                else:
                    value.n.x = value.n.x * data.lerp.mul[0] + data.lerp.add[0]
                    value.n.y = value.n.y * data.lerp.mul[1] + data.lerp.add[1]
                    value.n.z = value.n.z * data.lerp.mul[2] + data.lerp.add[2]
                if value.is_quaternion:
                    value.n.w = value.n.w * data.lerp.mul[3] + data.lerp.add[3]
            else:
                if value.is_tuple:
                    value.n.v = value.n.v
                else:
                    value.n.x = value.n.x
                    value.n.y = value.n.y
                    value.n.z = value.n.z
                if value.is_quaternion:
                    value.n.w = value.n.w
            print(' ' + str(value.n) + ' ' + str(value) + f' [{value.__class__.__name__}]')
        print('-----')

def retarget_action(target, reference):
    index = 0
    #while index < len(target.bone_headers):
    #    data = target.bone_headers[index]
    #    ref = reference.get_bone_header_by_id_and_usage(data.bone_id, data.usage)
    #    if not ref or (data.lerp and not ref.lerp) or (not data.lerp and ref.lerp):
    #        print(f'Bone#{data.bone_id} has no match {len(data.values)}')
    #        target.free_lerp_offsets.append(data.lerp_offset)
    #        del target.bone_headers[index]
    #        target.fcurve_count -= 1
    #        continue
    #    index += 1
    for data in target.bone_headers:
        ref = reference.get_bone_header_by_id_and_usage(data.bone_id, data.usage)
        if len(data.values) == 0:
            data.basis[0] = ref.basis[0]
            data.basis[1] = ref.basis[1]
            data.basis[2] = ref.basis[2]
            data.basis[3] = ref.basis[3]
        else:
            if ref.lerp and data.lerp:
                data.lerp.mul[0] = ref.lerp.mul[0]
                data.lerp.mul[1] = ref.lerp.mul[1]
                data.lerp.mul[2] = ref.lerp.mul[2]
                data.lerp.mul[3] = ref.lerp.mul[3]
                data.lerp.add[0] = ref.lerp.add[0]
                data.lerp.add[1] = ref.lerp.add[1]
                data.lerp.add[2] = ref.lerp.add[2]
                data.lerp.add[3] = ref.lerp.add[3]
            frame_one = data.values[0]
            if not frame_one.raw_value:
                mul = data.lerp.mul
                add = data.lerp.add
                if frame_one.is_tuple:
                    data.basis[0] = (frame_one.n.v * mul[0] + add[0])
                else:
                    data.basis[0] = (frame_one.n.x * mul[0] + add[0])
                    data.basis[1] = (frame_one.n.y * mul[1] + add[1])
                    data.basis[2] = (frame_one.n.z * mul[2] + add[2])
                if frame_one.is_quaternion:
                    data.basis[3] = (frame_one.n.w * mul[3] + add[3])
            else:
                if frame_one.is_tuple:
                    data.basis[0] = frame_one.n.v
                else:
                    data.basis[0] = frame_one.n.x
                    data.basis[1] = frame_one.n.y
                    data.basis[2] = frame_one.n.z
                if frame_one.is_quaternion:
                    data.basis[3] = frame_one.n.w
    target.write()

#print_basis_report(action_headers)

# Test reproducing original file.
#for action in action_headers:
#    action.normalize_keyframes()
#    action.denormalize_keyframes()
#    action.write()

#target1 = get_action_by_id(action_headers, 112)
#target1.normalize_keyframes()
#
#target1.denormalize_keyframes()
#target1.write()

#print_keyframes(target1, 328)

target1 = get_action_by_id(action_headers, 112)
#reference1 = get_action_by_id(action_headers, 199)
reference1 = get_action_by_id(action_headers, 108)

target2 = get_action_by_id(action_headers, 104)
#reference2 = get_action_by_id(action_headers, 110)
reference2 = get_action_by_id(action_headers, 106)

target3 = get_action_by_id(action_headers, 105)

target1.normalize_keyframes()
target2.normalize_keyframes()
target3.normalize_keyframes()
reference1.normalize_keyframes()
reference2.normalize_keyframes()

print('Rewriting Target #1')
retarget_action(target1, reference1)
print('Rewriting Target #2')
retarget_action(target2, reference2)
print('Rewriting Target #3')
retarget_action(target3, reference2)

with open(sys.argv[2], 'wb+') as out:
    seek(0)
    out.write(LMT_File.getbuffer())
