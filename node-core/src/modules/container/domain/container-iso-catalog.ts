// Equipment markings supported by the NexusPort catalog.
// Reference: BIC size/type tables and Hapag-Lloyd's container type groups.
const groups = [
  ['20GP', 6058, 2438, ['20G0']],
  ['20GP', 6058, 2591, ['22G0', '22G1']],
  ['40GP', 12192, 2591, ['42G0', '42G1']],
  ['40HC', 12192, 2896, ['45G0', '45G1']],
  ['45HC', 13716, 2896, ['L5G1']],
  ['20RF', 6058, 2591, ['22R1', '22R9']],
  ['40RF', 12192, 2591, ['42R1', '42R3', '42R9']],
  ['40RH', 12192, 2896, ['45R1', '45R9']],
  ['20OT', 6058, 2591, ['22U1', '22U6']],
  ['40OT', 12192, 2591, ['42U1', '42U6']],
  ['40OT', 12192, 2896, ['45U1', '45U6']],
  ['20FR', 6058, 2438, ['20P1']],
  ['20FR', 6058, 2591, ['22P1', '22P3', '22P8']],
  ['40FR', 12192, 2591, ['42P1', '42P3', '42P8']],
  ['40FR', 12192, 2896, ['45P3', '45P8']],
  ['20TK', 6058, 2438, ['20T5']],
  ['20TK', 6058, 2591, ['22T0', '22T5', '22T6']],
] as const;

export const containerIsoCatalog = groups.flatMap(([containerTypeCode, externalLengthMm, externalHeightMm, codes]) =>
  codes.map(isoSizeType => ({
    isoSizeType, containerTypeCode, externalLengthMm, externalWidthMm: 2438, externalHeightMm,
  })),
).sort((a, b) => a.isoSizeType.localeCompare(b.isoSizeType));
