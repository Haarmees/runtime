// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

.assembly extern legacy library mscorlib {}

.class public OR_U8 {
.method public static	int32 _or(int64,int64,int64) {
.maxstack		20
	ldarg	0
	ldarg	1
	or
	ldarg	2
	ceq
	ret
}

.method public static	int32 main(class [mscorlib]System.String[]) {
.entrypoint
.maxstack		20
	ldc.i8		0x0000000000000000
	ldc.i8		0x0000000000000000
	ldc.i8		0x0000000000000000
	call  int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x0000000000000000
	ldc.i8		0x0000000000000001
	ldc.i8		0x0000000000000001
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x0000000000000000
	ldc.i8		0x8000000000000000
	ldc.i8		0x8000000000000000
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x0000000000000000
	ldc.i8		0x5555555555555555
	ldc.i8		0x5555555555555555
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x0000000000000000
	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0xAAAAAAAAAAAAAAAA
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x0000000000000000
	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x0000000000000001
	ldc.i8		0x0000000000000000
	ldc.i8		0x0000000000000001
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x0000000000000001
	ldc.i8		0x0000000000000001
	ldc.i8		0x0000000000000001
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x0000000000000001
	ldc.i8		0x8000000000000000
	ldc.i8		0x8000000000000001
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x0000000000000001
	ldc.i8		0x5555555555555555
	ldc.i8		0x5555555555555555
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x0000000000000001
	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0xAAAAAAAAAAAAAAAB
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x0000000000000001
	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x8000000000000000
	ldc.i8		0x0000000000000000
	ldc.i8		0x8000000000000000
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x8000000000000000
	ldc.i8		0x0000000000000001
	ldc.i8		0x8000000000000001
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x8000000000000000
	ldc.i8		0x8000000000000000
	ldc.i8		0x8000000000000000
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x8000000000000000
	ldc.i8		0x5555555555555555
	ldc.i8		0xD555555555555555
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x8000000000000000
	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0xAAAAAAAAAAAAAAAA
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x8000000000000000
	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x5555555555555555
	ldc.i8		0x0000000000000000
	ldc.i8		0x5555555555555555
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x5555555555555555
	ldc.i8		0x0000000000000001
	ldc.i8		0x5555555555555555
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x5555555555555555
	ldc.i8		0x8000000000000000
	ldc.i8		0xD555555555555555
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x5555555555555555
	ldc.i8		0x5555555555555555
	ldc.i8		0x5555555555555555
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x5555555555555555
	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0x5555555555555555
	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0x0000000000000000
	ldc.i8		0xAAAAAAAAAAAAAAAA
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0x0000000000000001
	ldc.i8		0xAAAAAAAAAAAAAAAB
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0x8000000000000000
	ldc.i8		0xAAAAAAAAAAAAAAAA
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0x5555555555555555
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0xAAAAAAAAAAAAAAAA
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0x0000000000000000
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0x0000000000000001
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0x8000000000000000
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0x5555555555555555
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0xAAAAAAAAAAAAAAAA
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0xFFFFFFFFFFFFFFFF
	ldc.i8		0xFFFFFFFFFFFFFFFF
	call	int32 OR_U8::_or(int64,int64,int64)
	brfalse		FAIL

	ldc.i4 100
	ret
FAIL:
	ldc.i4	0x0
	ret
}
}
.assembly or_u8{}
