using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Carrier.Application.DTOs;
using NexusPort.Modules.Carrier.Application.Services;

namespace NexusPort.Modules.Carrier.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class CompaniesController : ControllerBase
{
    private readonly ICarrierService _carrierService;

    public CompaniesController(ICarrierService carrierService)
    {
        _carrierService = carrierService;
    }

    [HttpGet]
    // [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAll([FromQuery] string? search)
    {
        var companies = await _carrierService.GetAllAsync(search);
        return Ok(companies);
    }

    [HttpGet("{id}")]
    // [Authorize]
    public async Task<IActionResult> GetById(Guid id)
    {
        // TODO: Transport Company Authorization Rule Check
        var company = await _carrierService.GetByIdAsync(id);
        if (company == null) return NotFound();
        return Ok(company);
    }

    [HttpPost]
    // [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] CreateCompanyDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.CompanyName))
            return BadRequest("CompanyName is required");

        var company = await _carrierService.CreateAsync(dto);
        return CreatedAtAction(nameof(GetById), new { id = company.Id }, company);
    }

    [HttpPut("{id}")]
    // [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCompanyDto dto)
    {
        var company = await _carrierService.UpdateAsync(id, dto);
        if (company == null) return NotFound();
        return Ok(company);
    }

    [HttpPatch("{id}/status")]
    // [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeStatusDto dto)
    {
        var result = await _carrierService.ChangeStatusAsync(id, dto.Status);
        if (!result) return NotFound();
        return NoContent();
    }
}
