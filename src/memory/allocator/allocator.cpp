/************************************************************************************************
 *  SwiftlyS2 is a scripting framework for Source2-based games.
 *  Copyright (C) 2023-2026 Swiftly Solution SRL via Sava Andrei-Sebastian and it's contributors
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <https://www.gnu.org/licenses/>.
 ************************************************************************************************/

#include "allocator.h"
#include <cstring>
#include <algorithm>

#include "tier0/memdbgon.h"

void* MemoryAllocator::Alloc(uint64_t size)
{
    QueueLockGuard lock(m_mtxLock);
    void* ptr = malloc(size);
    if (ptr)
    {
        allocations[ptr] = size;
        totalAllocated += size;
    }
    return ptr;
}

void MemoryAllocator::Free(void* ptr)
{
    if (ptr == nullptr) return;

    QueueLockGuard lock(m_mtxLock);
    auto it = allocations.find(ptr);
    if (it != allocations.end())
    {
        totalAllocated -= it->second;
        allocations.erase(it);
        free(ptr);
    }
}

void* MemoryAllocator::Resize(void* ptr, uint64_t newSize)
{
    if (ptr == nullptr) return Alloc(newSize);
    QueueLockGuard lock(m_mtxLock);
    auto it = allocations.find(ptr);
    if (it != allocations.end())
    {
        uint64_t oldSize = it->second;
        void* newPtr = realloc(ptr, newSize);
        if (newPtr)
        {
            allocations.erase(it);
            allocations[newPtr] = newSize;
            totalAllocated = totalAllocated - oldSize + newSize;
            return newPtr;
        }
    }
    return nullptr;
}

uint64_t MemoryAllocator::GetSize(void* ptr)
{
    if (ptr == nullptr) return 0;

    auto it = allocations.find(ptr);
    if (it != allocations.end())
    {
        return it->second;
    }
    return 0;
}

uint64_t MemoryAllocator::GetTotalAllocated()
{
    return totalAllocated;
}

bool MemoryAllocator::IsPointerValid(void* ptr)
{
    if (ptr == nullptr) return false;
    return allocations.contains(ptr);
}

void MemoryAllocator::Copy(void* dest, void* src, uint64_t size)
{
    if (dest == nullptr || src == nullptr) return;
    memcpy(dest, src, size);
}

void MemoryAllocator::Move(void* dest, void* src, uint64_t size)
{
    if (dest == nullptr || src == nullptr) return;
    memmove(dest, src, size);
}

std::map<void*, uint64_t> MemoryAllocator::GetAllocations()
{
    return allocations;
}

MemoryAllocator::~MemoryAllocator()
{
    QueueLockGuard lock(m_mtxLock);
    for (const auto& [ptr, size] : allocations)
    {
        if (ptr == nullptr) continue; // not sure how it would be possible but just in case
        free(ptr);
    }
    allocations.clear();
    totalAllocated = 0;
}